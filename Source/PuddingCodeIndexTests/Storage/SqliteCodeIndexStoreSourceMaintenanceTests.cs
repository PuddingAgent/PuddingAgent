using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// D2：源维护状态的持久化门禁（持久 manifest / 消费者水位 / 维护账本 / 待重试）。
/// <para>
/// 锁定的语义：账本与 manifest 必须能精确往返（包括「没有基线」的 null 指纹与不完整行）；
/// 一批 manifest 写入是**单事务**（中途失败一字不落）；删除路径必须连消费者水位一起删；
/// 账本只前进（旧世代 / 旧期望版本的回退写入被拒绝）且待重试与消费者水位整体替换。
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreSourceMaintenanceTests
{
    private const string WorkspaceId = "ws-source";
    private const string ProjectId = "scope-source";

    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    // ── 默认与往返 ──────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task FreshDatabase_HasAnEmptyManifestAndADefaultLedger()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);

        Assert.IsEmpty(snapshot.Manifest);
        Assert.AreEqual(0, snapshot.Ledger.Epoch);
        Assert.AreEqual(0, snapshot.Ledger.DesiredVersion);
        Assert.AreEqual(0, snapshot.Ledger.CommittedVersion);
        Assert.IsNull(snapshot.Ledger.ScanWatermarkUtc);
        Assert.IsFalse(snapshot.Ledger.DirtyAgain);
        Assert.IsEmpty(snapshot.Ledger.PendingRetries);
        Assert.IsEmpty(snapshot.Ledger.ConsumerAppliedVersions);
    }

    [TestMethod]
    public async Task Manifest_RoundTripsFingerprintAppliedVersionsAndCompleteness()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var path = fixture.File("src/A.cs");
        var other = fixture.File("src/B.cs");

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [
                new CodeSourceEntry(
                    path,
                    new SourceFingerprint(T0.AddMinutes(5), 128, "hash-a"),
                    [
                        new AppliedFileVersion("csharp", "policy-1", "semantic-1", 7),
                        new AppliedFileVersion("fulltext", "policy-f", "semantic-f", 3),
                    ]),
                new CodeSourceEntry(
                    other,
                    new SourceFingerprint(T0.AddMinutes(6), 64, "hash-b"),
                    [new AppliedFileVersion("csharp", "policy-2", "semantic-2", 9)],
                    Complete: false),
            ],
            []);

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);

        Assert.HasCount(2, snapshot.Manifest);
        var loaded = snapshot.Manifest[path];
        Assert.IsNotNull(loaded.Fingerprint);
        Assert.AreEqual(T0.AddMinutes(5), loaded.Fingerprint!.LastWriteTimeUtc);
        Assert.AreEqual(128, loaded.Fingerprint.Length);
        Assert.AreEqual("hash-a", loaded.Fingerprint.ContentHash);
        Assert.IsTrue(loaded.Complete);
        Assert.HasCount(2, loaded.AppliedVersions);
        var csharp = loaded.AppliedVersions.Single(v => v.ProviderId == "csharp");
        Assert.AreEqual("policy-1", csharp.ParserPolicyFingerprint);
        Assert.AreEqual("semantic-1", csharp.SemanticInputFingerprint);
        Assert.AreEqual(7, csharp.AppliedVersion);

        Assert.IsFalse(snapshot.Manifest[other].Complete, "不完整的行必须原样保留");
    }

    [TestMethod]
    public async Task Manifest_RoundTripsARowWithoutABaselineFingerprint()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var path = fixture.File("src/never-committed.cs");

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, Fingerprint: null, AppliedVersions: [])],
            []);

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);

        Assert.IsNull(
            snapshot.Manifest[path].Fingerprint,
            "「首次没有基线」必须可表达：不得把 null 指纹伪造成某个默认值");
        Assert.IsEmpty(snapshot.Manifest[path].AppliedVersions);
    }

    [TestMethod]
    public async Task ManifestUpsert_ReplacesTheConsumerSetInsteadOfMergingIt()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var path = fixture.File("src/A.cs");

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h1"),
                [new AppliedFileVersion("csharp", "p", "s", 1), new AppliedFileVersion("fulltext", "p", "s", 1)])],
            []);

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0.AddMinutes(1), 2, "h2"),
                [new AppliedFileVersion("csharp", "p", "s", 2)])],
            []);

        var loaded = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Manifest[path];

        Assert.HasCount(1, loaded.AppliedVersions, "第二次写入就是该路径消费者水位的完整集合");
        Assert.AreEqual(2, loaded.AppliedVersions[0].AppliedVersion);
        Assert.AreEqual("h2", loaded.Fingerprint!.ContentHash);
    }

    // ── 原子性与删除 ────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveSourceManifest_LeavesNothingBehindWhenOneRowFails()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var first = fixture.File("src/First.cs");
        var failing = fixture.File("src/Failing.cs");
        var removed = fixture.File("src/Removed.cs");

        // 先放入一条待删除的行，用来证明失败时删除也不会生效。
        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(removed, new SourceFingerprint(T0, 1, "old"), [])],
            []);

        await fixture.FailManifestInsertAsync(failing);

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [
                new CodeSourceEntry(first, new SourceFingerprint(T0, 1, "h1"), []),
                new CodeSourceEntry(failing, new SourceFingerprint(T0, 1, "h2"), []),
            ],
            [removed]));

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);

        Assert.IsFalse(snapshot.Manifest.ContainsKey(first), "同一批次里先写入的行必须一起回滚");
        Assert.IsTrue(snapshot.Manifest.ContainsKey(removed), "同一批次里的删除也必须一起回滚");
    }

    [TestMethod]
    public async Task RemovingAPathAlsoRemovesItsAppliedVersions()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var path = fixture.File("src/A.cs");

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h1"),
                [new AppliedFileVersion("csharp", "p", "s", 4)])],
            []);

        await store.SaveSourceManifestAsync(WorkspaceId, ProjectId, [], [path]);

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);
        Assert.IsEmpty(snapshot.Manifest);

        // 同名新文件不得继承旧文件的「已应用」状态。
        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0.AddMinutes(9), 2, "h2"), [])],
            []);

        var reloaded = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Manifest[path];
        Assert.IsEmpty(reloaded.AppliedVersions);
    }

    [TestMethod]
    public async Task ManifestAndLedger_AreScopedByWorkspaceAndProject()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var path = fixture.File("src/A.cs");
        const string otherProject = "scope-other";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h1"), [])],
            []);
        await store.SaveSourceManifestAsync(
            "ws-other",
            otherProject,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "other"), [])],
            []);

        var mine = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);
        var theirs = await store.LoadSourceMaintenanceAsync("ws-other", otherProject);

        Assert.AreEqual("h1", mine.Manifest[path].Fingerprint!.ContentHash);
        Assert.AreEqual("other", theirs.Manifest[path].Fingerprint!.ContentHash);
    }

    // ── 账本 ────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Ledger_RoundTripsVersionsWatermarkDirtyFlagAndConsumerWatermarks()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var retry = new CodeSourceRetry(
            fixture.File("src/A.cs"), "extract_failed", 2, T0.AddMinutes(1), T0.AddMinutes(3));

        var accepted = await store.SaveMaintenanceLedgerAsync(
            WorkspaceId,
            ProjectId,
            new CodeSourceMaintenanceLedgerState(
                WorkspaceId,
                ProjectId,
                Epoch: 4,
                DesiredVersion: 11,
                CommittedVersion: 9,
                ConsumerAppliedVersions: new Dictionary<string, long> { ["csharp"] = 9, ["fulltext"] = 5 },
                PendingRetries: new Dictionary<string, CodeSourceRetry> { [retry.FilePath] = retry },
                ScanWatermarkUtc: T0.AddMinutes(10),
                DirtyAgain: true));

        Assert.IsTrue(accepted);

        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;

        Assert.AreEqual(4, ledger.Epoch);
        Assert.AreEqual(11, ledger.DesiredVersion);
        Assert.AreEqual(9, ledger.CommittedVersion);
        Assert.AreEqual(T0.AddMinutes(10), ledger.ScanWatermarkUtc);
        Assert.IsTrue(ledger.DirtyAgain);
        Assert.AreEqual(9, ledger.ConsumerAppliedVersions["csharp"]);
        Assert.AreEqual(5, ledger.ConsumerAppliedVersions["fulltext"]);

        var loadedRetry = ledger.PendingRetries[retry.FilePath];
        Assert.AreEqual("extract_failed", loadedRetry.Reason);
        Assert.AreEqual(2, loadedRetry.Attempts);
        Assert.AreEqual(T0.AddMinutes(1), loadedRetry.FirstFailedAtUtc);
        Assert.AreEqual(T0.AddMinutes(3), loadedRetry.NextAttemptAtUtc);
    }

    [TestMethod]
    public async Task SaveMaintenanceLedger_ReplacesTheRetryAndWatermarkSets()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var first = fixture.File("src/First.cs");
        var second = fixture.File("src/Second.cs");

        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId,
            ProjectId,
            new CodeSourceMaintenanceLedgerState(
                WorkspaceId, ProjectId, 1, 1, 0,
                new Dictionary<string, long> { ["csharp"] = 1 },
                new Dictionary<string, CodeSourceRetry>
                {
                    [first] = new(first, "failed", 1, T0, T0.AddMinutes(1)),
                },
                ScanWatermarkUtc: null,
                DirtyAgain: true));

        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId,
            ProjectId,
            new CodeSourceMaintenanceLedgerState(
                WorkspaceId, ProjectId, 1, 2, 2,
                new Dictionary<string, long> { ["fulltext"] = 2 },
                new Dictionary<string, CodeSourceRetry>
                {
                    [second] = new(second, "failed", 1, T0, T0.AddMinutes(1)),
                },
                ScanWatermarkUtc: T0.AddMinutes(20),
                DirtyAgain: false));

        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;

        Assert.HasCount(1, ledger.PendingRetries);
        Assert.IsTrue(ledger.PendingRetries.ContainsKey(second), "上一次的待重试不得残留");
        Assert.IsFalse(ledger.ConsumerAppliedVersions.ContainsKey("csharp"), "消费者水位也是整体替换");
        Assert.AreEqual(2, ledger.ConsumerAppliedVersions["fulltext"]);
        Assert.AreEqual(T0.AddMinutes(20), ledger.ScanWatermarkUtc);
    }

    [TestMethod]
    public async Task SaveMaintenanceLedger_RejectsARegressedEpoch()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 5, desired: 10, committed: 8, watermark: T0.AddMinutes(30)));

        // 旧世代的迟到写入：拒绝，存储保持原值。
        var accepted = await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 4, desired: 99, committed: 99, watermark: T0.AddMinutes(40)));

        Assert.IsFalse(accepted);
        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;
        Assert.AreEqual(5, ledger.Epoch);
        Assert.AreEqual(10, ledger.DesiredVersion);
        Assert.AreEqual(T0.AddMinutes(30), ledger.ScanWatermarkUtc);
    }

    [TestMethod]
    public async Task SaveMaintenanceLedger_RejectsARegressedDesiredVersionInTheSameEpoch()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 2, desired: 10, committed: 10, watermark: T0.AddMinutes(30)));

        var accepted = await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 2, desired: 9, committed: 9, watermark: null));

        Assert.IsFalse(accepted);
        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;
        Assert.AreEqual(10, ledger.DesiredVersion);
        Assert.AreEqual(T0.AddMinutes(30), ledger.ScanWatermarkUtc, "水位也不得被这次回退写入清空");
    }

    [TestMethod]
    public async Task SaveMaintenanceLedger_AcceptsANewerEpochEvenWithLowerVersions()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 2, desired: 10, committed: 10, watermark: T0.AddMinutes(30)));

        // 新世代意味着 scope 被重建：版本号从头开始是合法的。
        var accepted = await store.SaveMaintenanceLedgerAsync(
            WorkspaceId, ProjectId, LedgerState(epoch: 3, desired: 1, committed: 0, watermark: null, dirty: true));

        Assert.IsTrue(accepted);
        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;
        Assert.AreEqual(3, ledger.Epoch);
        Assert.AreEqual(1, ledger.DesiredVersion);
        Assert.IsNull(ledger.ScanWatermarkUtc);
    }

    [TestMethod]
    public async Task SaveMaintenanceLedger_RejectsAMismatchedScope()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveMaintenanceLedgerAsync(
            WorkspaceId,
            ProjectId,
            LedgerState(epoch: 1, desired: 1, committed: 0, watermark: null) with { ScopeId = "other-scope" }));
    }

    // ── 幂等 ────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SchemaBootstrap_IsIdempotentAcrossStoreInstances()
    {
        using var fixture = Fixture.Create();
        var path = fixture.File("src/A.cs");

        await fixture.Store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h1"), [])],
            []);

        // 同一库上的第二个 store 实例：重复 bootstrap 不得丢数据，也不得报错。
        var second = new SqliteCodeIndexStore(fixture.DatabasePath);
        await second.InitializeAsync();
        await second.InitializeAsync();

        var snapshot = await second.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);
        Assert.AreEqual("h1", snapshot.Manifest[path].Fingerprint!.ContentHash);
    }

    private static CodeSourceMaintenanceLedgerState LedgerState(
        long epoch,
        long desired,
        long committed,
        DateTimeOffset? watermark,
        bool dirty = false) =>
        new(
            WorkspaceId,
            ProjectId,
            epoch,
            desired,
            committed,
            new Dictionary<string, long>(),
            new Dictionary<string, CodeSourceRetry>(),
            watermark,
            dirty);

    /// <summary>临时目录里的真实 SQLite 库 + 产品 provider。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string databasePath)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = new SqliteCodeIndexStore(databasePath);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteCodeIndexStore Store { get; }

        public string File(string relativePath) =>
            System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public static Fixture Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pudding-d2-source-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, System.IO.Path.Combine(root, "db", "code-index.db"));
        }

        /// <summary>给某个路径的 manifest 写入注入失败（验证整批回滚）。</summary>
        public async Task FailManifestInsertAsync(string filePath)
        {
            await Store.InitializeAsync();

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync();

            var escaped = filePath.Replace("'", "''", StringComparison.Ordinal);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS d2_fail_manifest_insert
                BEFORE INSERT ON CodeSourceManifest
                WHEN NEW.FilePath = '{escaped}'
                BEGIN
                    SELECT RAISE(ABORT, 'D2 injected manifest failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // temp 目录清理是 best-effort
            }
        }
    }
}
