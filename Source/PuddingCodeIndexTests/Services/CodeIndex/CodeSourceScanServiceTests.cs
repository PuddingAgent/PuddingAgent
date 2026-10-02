using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// 完整清单校准的门禁（D2 §2）：磁盘事实 + 持久 manifest + 账本 → 真实变更集 + 捕获版本。
/// <para>
/// 关键语义：扫描**不写索引**、**不推进扫描水位**（水位只在执行层回报提交时前进）；
/// 根不可用/扫描不完整时绝不产出删除；被忽略的旧路径在完整扫描下才会成为删除候选。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceScanServiceTests
{
    private const string WorkspaceId = "ws-scan";
    private const string ProjectId = "scope-scan";
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static CodeSourceScanService Service(
        FakeScanner scanner,
        ICodeSourceMaintenanceStore? store,
        DateTimeOffset? now = null) =>
        new(
            scanner,
            store,
            new FixedTimeProvider(now ?? T0.AddHours(1)),
            logger: NullLogger<CodeSourceScanService>.Instance);

    [TestMethod]
    public async Task CompleteScan_ProducesNewChangedAndDeletedCandidates()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var changed = @"C:\repo\src\changed.cs";
        var deleted = @"C:\repo\src\gone.cs";
        var fresh = @"C:\repo\src\fresh.cs";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [
                new CodeSourceEntry(changed, new SourceFingerprint(T0, 10, "h1"),
                    [new AppliedFileVersion("csharp", "p", "s", 1)]),
                new CodeSourceEntry(deleted, new SourceFingerprint(T0, 10, "h2"),
                    [new AppliedFileVersion("csharp", "p", "s", 1)]),
            ],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [
                new CodeSourceDiskEntry(changed, T0.AddMinutes(5), 11),
                new CodeSourceDiskEntry(fresh, T0.AddMinutes(1), 3),
            ],
            RootUsable: true,
            Complete: true,
            IncompleteReason: null));

        var run = await Service(scanner, store).RunAsync(
            WorkspaceId,
            ProjectId,
            @"C:\repo",
            new CodeSourceScanOptions(ConsumerInputs: [new CodeConsumerInputFingerprint("csharp", "p", "s")]));

        Assert.IsFalse(run.CapabilityMissing);
        Assert.IsTrue(run.ChangeSet.ScanComplete);
        Assert.AreEqual(2, run.ChangeSet.ReindexCount, "变更文件 + 新文件各一条需要读取内容");
        Assert.AreEqual(1, run.ChangeSet.DeletedCount, "完整扫描才确认删除");
        Assert.AreEqual(1, run.CapturedVersion);

        var byPath = run.ChangeSet.Changes.ToDictionary(change => change.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeSourceAction.ReindexContent, byPath[changed].Action);
        Assert.AreEqual(CodeSourceAction.Delete, byPath[deleted].Action);
        Assert.AreEqual(CodeSourceAction.ReindexContent, byPath[fresh].Action);
        Assert.IsTrue(byPath[fresh].RequiresContentHash, "新文件必须先稳定读取再提交");
    }

    [TestMethod]
    public async Task Scan_RecordsTheDesiredVersionButNeverAdvancesTheScanWatermark()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;

        // 账本里已有一个成功水位：扫描不得改写它。
        await store.SaveMaintenanceLedgerAsync(
            WorkspaceId,
            ProjectId,
            new CodeSourceMaintenanceLedgerState(
                WorkspaceId, ProjectId, 1, 5, 5,
                new Dictionary<string, long>(),
                new Dictionary<string, CodeSourceRetry>(),
                ScanWatermarkUtc: T0,
                DirtyAgain: false));

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [new CodeSourceDiskEntry(@"C:\repo\a.cs", T0, 1)], RootUsable: true, Complete: true, IncompleteReason: null));

        var run = await Service(scanner, store).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        Assert.AreEqual(6, run.CapturedVersion, "在已持久化的期望版本上递增");

        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;
        Assert.AreEqual(6, ledger.DesiredVersion);
        Assert.AreEqual(T0, ledger.ScanWatermarkUtc, "扫描不推进水位：只有执行层回报提交时才前进");
        Assert.IsTrue(ledger.DirtyAgain, "登记了新工作 ⇒ 账本标记还有工作");
    }

    [TestMethod]
    public async Task Scan_WithNoChanges_PersistsNothing()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var path = @"C:\repo\a.cs";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h"), [new AppliedFileVersion("csharp", "p", "s", 1)])],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [new CodeSourceDiskEntry(path, T0, 1)], RootUsable: true, Complete: true, IncompleteReason: null));

        var run = await Service(scanner, store).RunAsync(
            WorkspaceId,
            ProjectId,
            @"C:\repo",
            new CodeSourceScanOptions(ConsumerInputs: [new CodeConsumerInputFingerprint("csharp", "p", "s")]));

        Assert.IsEmpty(run.ChangeSet.Changes);
        Assert.AreEqual(0, run.CapturedVersion, "没有需要动作的路径就不登记新版本");

        var ledger = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Ledger;
        Assert.AreEqual(0, ledger.DesiredVersion);
        Assert.IsFalse(ledger.DirtyAgain);
    }

    [TestMethod]
    public async Task UnusableRoot_NeverProducesDeletions()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var path = @"C:\repo\gone.cs";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h"), [])],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [], RootUsable: false, Complete: false, IncompleteReason: CodeSourceScanReasons.RootUnusable));

        var run = await Service(scanner, store).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        Assert.AreEqual(0, run.ChangeSet.DeletedCount, "根不可用时「不在磁盘上」对所有路径都成立，绝不能删");
        Assert.AreEqual(1, run.ChangeSet.UnresolvedMissingCount);
        Assert.AreEqual(CodeSourceAction.Deferred, run.ChangeSet.Changes.Single().Action);
        Assert.AreEqual(1, run.CapturedVersion, "未解决的路径就是待办：必须登记进账本");
    }

    [TestMethod]
    public async Task IncompleteScan_NeverProducesDeletions()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var path = @"C:\repo\gone.cs";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h"), [])],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [], RootUsable: true, Complete: false, IncompleteReason: CodeSourceScanReasons.SubtreeUnreadable));

        var run = await Service(scanner, store).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        Assert.AreEqual(0, run.ChangeSet.DeletedCount);
        Assert.AreEqual(1, run.ChangeSet.UnresolvedMissingCount);
        Assert.IsFalse(run.ChangeSet.ScanComplete);
    }

    [TestMethod]
    public async Task PathsExcludedByChangedIgnoreRulesBecomeDeletionCandidates()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var excluded = @"C:\repo\docs\old.md";

        // 旧规则下它被索引过；新规则把它排除，于是完整扫描看不到它 —— 这正是「规则变更 ⇒ 移除记录」。
        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(excluded, new SourceFingerprint(T0, 1, "h"), [])],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [], RootUsable: true, Complete: true, IncompleteReason: null));

        var run = await Service(scanner, store).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        var change = run.ChangeSet.Changes.Single();
        Assert.AreEqual(CodeSourceAction.Delete, change.Action);
        Assert.AreEqual(1, run.ChangeSet.DeletedCount);
    }

    [TestMethod]
    public async Task StoreWithoutTheCapability_StillReportsDiskFacts()
    {
        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [new CodeSourceDiskEntry(@"C:\repo\a.cs", T0, 1)], RootUsable: true, Complete: true, IncompleteReason: null));

        var run = await Service(scanner, store: null).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        Assert.IsTrue(run.CapabilityMissing);
        Assert.AreEqual(1, run.ChangeSet.ReindexCount, "没有 manifest 时磁盘上的文件都是候选");
        Assert.AreEqual(1, run.CapturedVersion, "即使没有持久账本，本次也登记一个期望版本");
    }

    [TestMethod]
    public async Task DeepVerifyAndWatcherHints_ArePassedThroughToTheDetector()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var path = @"C:\repo\a.cs";

        await store.SaveSourceManifestAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSourceEntry(path, new SourceFingerprint(T0, 1, "h"), [new AppliedFileVersion("csharp", "p", "s", 1)])],
            []);

        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [new CodeSourceDiskEntry(path, T0, 1)], RootUsable: true, Complete: true, IncompleteReason: null));

        var run = await Service(scanner, store).RunAsync(
            WorkspaceId,
            ProjectId,
            @"C:\repo",
            new CodeSourceScanOptions(
                WatcherHints: [path],
                ConsumerInputs: [new CodeConsumerInputFingerprint("csharp", "p", "s")],
                DeepVerify: true));

        var change = run.ChangeSet.Changes.Single();
        Assert.AreEqual(CodeSourceAction.ReindexContent, change.Action);
        Assert.IsTrue(change.RequiresContentHash);
        Assert.IsTrue(change.Reasons.Contains(CodeSourceChangeReasons.DeepVerify));
        Assert.IsTrue(change.Reasons.Contains(CodeSourceChangeReasons.WatcherHint));
    }

    [TestMethod]
    public async Task ScanUsesItsOwnClockWhenNoScanStartIsGiven()
    {
        using var fixture = StoreFixture.Create();
        var scanner = new FakeScanner(new CodeSourceScanOutcome(
            [new CodeSourceDiskEntry(@"C:\repo\a.cs", T0, 1)], RootUsable: true, Complete: true, IncompleteReason: null));
        var now = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

        var run = await Service(scanner, fixture.Store, now).RunAsync(WorkspaceId, ProjectId, @"C:\repo");

        Assert.AreEqual(now, run.ScanStartedUtc, "执行层要用它回报提交（水位只在那里推进）");
    }

    /// <summary>可编程的枚举替身：只回报给定的磁盘事实。</summary>
    private sealed class FakeScanner : ICodeSourceScanner
    {
        private readonly CodeSourceScanOutcome _outcome;

        public FakeScanner(CodeSourceScanOutcome outcome) => _outcome = outcome;

        public string? LastRootPath { get; private set; }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            LastRootPath = rootPath;
            return Task.FromResult(_outcome);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>真实 SQLite 库（产品 provider）上的 store 夹具。</summary>
    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, string databasePath)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = new SqliteCodeIndexStore(databasePath);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteCodeIndexStore Store { get; }

        public static StoreFixture Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pudding-d2-scanservice-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new StoreFixture(root, System.IO.Path.Combine(root, "db", "code-index.db"));
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
