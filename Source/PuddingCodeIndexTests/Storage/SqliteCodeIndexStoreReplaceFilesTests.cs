using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// D4：原子文件替换（<c>ReplaceFilesAsync</c>）门禁。
/// <para>
/// 锁定的核心不是「写得进去」，而是三件容易写错的事：
/// ① <b>所有权</b>——本文件的出边/引用全量重建；
/// ② <b>依赖</b>——指向仍存在符号的入边必须保留，只有指向消失符号的入边被删除，且其来源文件被报告为需重新绑定；
/// ③ <b>原子性</b>——索引结果与源指纹同一事务，任何一步失败都保留旧完整产物。
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreReplaceFilesTests
{
    private const string WorkspaceId = "ws-replace";
    private const string ProjectId = "scope-replace";

    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Replace_WritesIndexRowsAndSourceFingerprintInOneBatch()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/A.cs");

        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(
                    file,
                    ["sym-a1"],
                    references: [Reference("sym-a1", "sym-b1", file, 3)],
                    relations: [Relation("sym-a1", "sym-b1", file, 3)],
                    fingerprint: new SourceFingerprint(T0.AddMinutes(1), 42, "hash-1"),
                    applied: [new AppliedFileVersion("csharp", "p1", "s1", 1)]),
            ]);

        Assert.AreEqual(1, result.ReplacedFileCount);
        Assert.IsEmpty(result.RemovedSymbolIds);
        Assert.IsEmpty(result.InvalidatedDependentFilePaths);

        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, file));
        Assert.HasCount(1, await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-b1"));
        Assert.HasCount(1, await store.ListRelationsAsync(WorkspaceId, ProjectId, "sym-a1"));

        var snapshot = await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId);
        var manifest = snapshot.Manifest[file];
        Assert.AreEqual("hash-1", manifest.Fingerprint!.ContentHash);
        Assert.AreEqual(42, manifest.Fingerprint.Length);
        Assert.HasCount(1, manifest.AppliedVersions);
        Assert.AreEqual(1, manifest.AppliedVersions[0].AppliedVersion);
    }

    [TestMethod]
    public async Task Replace_RebuildsOwnedEdgesAndDropsTheOldSymbols()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/A.cs");

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(
                    file,
                    ["sym-old"],
                    references: [Reference("sym-old", "sym-target", file, 1)],
                    relations: [Relation("sym-old", "sym-target", file, 1)],
                    fingerprint: new SourceFingerprint(T0, 10, "h1")),
            ]);

        // 同一文件、同样的符号：应当是全量重建而不是叠加。
        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(
                    file,
                    ["sym-new"],
                    references: [Reference("sym-new", "sym-target", file, 2)],
                    relations: [Relation("sym-new", "sym-target", file, 2)],
                    fingerprint: new SourceFingerprint(T0.AddMinutes(2), 20, "h2")),
            ]);

        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-old"));
        Assert.IsNotNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-new"));
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-old"));
        // 旧引用行必须被重建而不是叠加：指向 sym-target 的引用只剩新符号这一条。
        var referencesToTarget = await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-target");
        Assert.HasCount(1, referencesToTarget);
        Assert.AreEqual("sym-new", referencesToTarget[0].SourceSymbolId);
        Assert.HasCount(1, await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-new"));
        Assert.HasCount(1, await store.ListRelationsAsync(WorkspaceId, ProjectId, "sym-new"));
        Assert.IsEmpty(await store.ListRelationsAsync(WorkspaceId, ProjectId, "sym-old"));
    }

    [TestMethod]
    public async Task Replace_KeepsIncomingEdgesToSurvivingSymbols()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var a = fixture.File("src/A.cs");
        var b = fixture.File("src/B.cs");

        // B 指向 A 的稳定符号：替换 A 之后这条入边必须仍在（它属于 B）。
        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(a, ["sym-keep"], fingerprint: new SourceFingerprint(T0, 10, "h1")),
                Replacement(
                    b,
                    ["sym-b"],
                    references: [Reference("sym-b", "sym-keep", b, 1)],
                    relations: [Relation("sym-b", "sym-keep", b, 1)],
                    fingerprint: new SourceFingerprint(T0, 10, "h2")),
            ]);

        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [Replacement(a, ["sym-keep"], fingerprint: new SourceFingerprint(T0.AddMinutes(1), 11, "h1b"))]);

        Assert.IsEmpty(result.RemovedSymbolIds);
        Assert.IsEmpty(result.InvalidatedDependentFilePaths);
        Assert.HasCount(1, await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-keep"), "指向仍存在符号的入边必须保留");
        Assert.HasCount(1, await store.ListRelationsAsync(WorkspaceId, ProjectId, "sym-b"));
    }

    [TestMethod]
    public async Task Replace_ReportsDependentsWhenATargetSymbolDisappears()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var a = fixture.File("src/A.cs");
        var b = fixture.File("src/B.cs");
        var c = fixture.File("src/C.cs");

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(a, ["sym-vanishing"], fingerprint: new SourceFingerprint(T0, 10, "h1")),
                Replacement(
                    b,
                    ["sym-b"],
                    references: [Reference("sym-b", "sym-vanishing", b, 1)],
                    fingerprint: new SourceFingerprint(T0, 10, "h2")),
                Replacement(
                    c,
                    ["sym-c"],
                    relations: [Relation("sym-c", "sym-vanishing", c, 1)],
                    fingerprint: new SourceFingerprint(T0, 10, "h3")),
            ]);

        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [Replacement(a, ["sym-replacement"], fingerprint: new SourceFingerprint(T0.AddMinutes(1), 11, "h1b"))]);

        CollectionAssert.AreEquivalent(
            new[] { b, c },
            result.InvalidatedDependentFilePaths.ToArray(),
            "失去入边的其他文件必须被报告出来，调用方才能安排重新绑定");
        CollectionAssert.AreEquivalent(
            new[] { "sym-vanishing" },
            result.RemovedSymbolIds.ToArray());
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-vanishing"));

        // 依赖方指向「已消失目标」的行必须清除（它指向的符号不存在了），并由上面的报告驱动重新绑定；
        // 但 C 自己的**符号**不受影响。
        Assert.IsEmpty(
            await store.ListRelationsAsync(WorkspaceId, ProjectId, "sym-c"),
            "指向消失符号的出边失效，等待 C 重新绑定时重建");
        Assert.IsNotNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-c"));
    }

    [TestMethod]
    public async Task Replace_DoesNotReportFilesThatArePartOfTheSameBatch()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var a = fixture.File("src/A.cs");
        var b = fixture.File("src/B.cs");

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(a, ["sym-a-old"], fingerprint: new SourceFingerprint(T0, 10, "h1")),
                Replacement(
                    b,
                    ["sym-b"],
                    references: [Reference("sym-b", "sym-a-old", b, 1)],
                    fingerprint: new SourceFingerprint(T0, 10, "h2")),
            ]);

        // 两个文件同批重建：B 的入边随它自己的重建一起消失，不应被当成「失效依赖」。
        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(a, ["sym-a-new"], fingerprint: new SourceFingerprint(T0.AddMinutes(1), 11, "h1b")),
                Replacement(b, ["sym-b"], fingerprint: new SourceFingerprint(T0.AddMinutes(1), 12, "h2b")),
            ]);

        Assert.IsEmpty(result.InvalidatedDependentFilePaths);
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, "sym-a-old"));
    }

    [TestMethod]
    public async Task Replace_WithNoSymbolsRemovesTheOldOnesButKeepsTheFileRecord()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/A.cs");

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [Replacement(file, ["sym-old"], fingerprint: new SourceFingerprint(T0, 10, "h1"))]);

        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [Replacement(file, [], fingerprint: new SourceFingerprint(T0.AddMinutes(1), 2, "h2"))]);

        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-old"));
        Assert.HasCount(1, await store.ListFilesAsync(WorkspaceId, ProjectId), "文件记录仍在（只是没有符号）");
        Assert.AreEqual("h2", (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Manifest[file].Fingerprint!.ContentHash);
    }

    [TestMethod]
    public async Task Replace_KeepsTheOldResultWhenAnythingInTheBatchFails()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var first = fixture.File("src/First.cs");
        var failing = fixture.File("src/Failing.cs");

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(first, ["sym-first-old"], fingerprint: new SourceFingerprint(T0, 10, "h1")),
            ]);

        await fixture.FailManifestWriteAsync(failing);

        await Assert.ThrowsAsync<SqliteException>(() => store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(first, ["sym-first-new"], fingerprint: new SourceFingerprint(T0.AddMinutes(5), 50, "h1-new")),
                Replacement(failing, ["sym-failing"], fingerprint: new SourceFingerprint(T0.AddMinutes(5), 60, "h2-new")),
            ]));

        // 旧完整产物必须原样保留（D4：不能提前 clear 后再提取）。
        Assert.IsNotNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-first-old"));
        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-first-new"));
        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-failing"));

        var manifest = (await store.LoadSourceMaintenanceAsync(WorkspaceId, ProjectId)).Manifest;
        Assert.AreEqual("h1", manifest[first].Fingerprint!.ContentHash, "指纹也不得半更新");
        Assert.IsFalse(manifest.ContainsKey(failing));
    }

    [TestMethod]
    public async Task Replace_IsScopedAndRejectsAMismatchedSourcePath()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/A.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                new CodeSourceFileReplacement(
                    new CodeFileRecord(WorkspaceId, ProjectId, file),
                    [],
                    [],
                    [],
                    new CodeSourceEntry(fixture.File("src/Other.cs"), null, [])),
            ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                new CodeSourceFileReplacement(
                    new CodeFileRecord("ws-other", ProjectId, file),
                    [],
                    [],
                    [],
                    new CodeSourceEntry(file, null, [])),
            ]));
    }

    [TestMethod]
    public async Task Replace_RejectsTheSameFileTwiceInOneBatch()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/A.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(file, ["sym-a"], fingerprint: new SourceFingerprint(T0, 10, "h1")),
                Replacement(file, ["sym-b"], fingerprint: new SourceFingerprint(T0, 11, "h2")),
            ]));
    }

    [TestMethod]
    public async Task Replace_HandlesMoreSymbolsThanOneInClauseAllows()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;
        var file = fixture.File("src/Big.cs");
        var keep = fixture.File("src/Keep.cs");

        var manyOld = Enumerable.Range(0, 400).Select(index => $"sym-old-{index}").ToArray();
        var manyNew = Enumerable.Range(0, 400).Select(index => $"sym-new-{index}").ToArray();

        await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [
                Replacement(file, manyOld, fingerprint: new SourceFingerprint(T0, 10, "h1")),
                Replacement(
                    keep,
                    ["sym-keep"],
                    references:
                    [
                        new CodeReferenceRecord(WorkspaceId, ProjectId, "sym-keep", "sym-old-399", keep, 1, null, T0),
                        new CodeReferenceRecord(WorkspaceId, ProjectId, "sym-keep", "sym-old-0", keep, 2, null, T0),
                    ],
                    fingerprint: new SourceFingerprint(T0, 10, "h2")),
            ]);

        var result = await store.ReplaceFilesAsync(
            WorkspaceId,
            ProjectId,
            [Replacement(file, manyNew, fingerprint: new SourceFingerprint(T0.AddMinutes(1), 11, "h1b"))]);

        Assert.HasCount(400, result.RemovedSymbolIds, "分块删除必须覆盖全部旧符号");
        Assert.AreEqual(keep, result.InvalidatedDependentFilePaths.Single());
        Assert.HasCount(400, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, file));
        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-old-0"));
        Assert.IsNotNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, "sym-new-399"));
    }

    private static CodeSourceFileReplacement Replacement(
        string filePath,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<CodeReferenceRecord>? references = null,
        IReadOnlyList<CodeRelationRecord>? relations = null,
        SourceFingerprint? fingerprint = null,
        IReadOnlyList<AppliedFileVersion>? applied = null) =>
        new(
            new CodeFileRecord(WorkspaceId, ProjectId, filePath, "C#", T0),
            symbolIds.Select(id => new CodeSymbolRecord(
                WorkspaceId, ProjectId, filePath, id, id, CodeSymbolKind.Class, 1, 5, $"class {id}", null)).ToArray(),
            references ?? [],
            relations ?? [],
            new CodeSourceEntry(filePath, fingerprint, applied ?? []));

    private static CodeReferenceRecord Reference(string source, string target, string filePath, int line) =>
        new(WorkspaceId, ProjectId, source, target, filePath, line, null, T0);

    private static CodeRelationRecord Relation(string source, string target, string filePath, int line) =>
        new(WorkspaceId, ProjectId, source, target, CodeRelationKind.Calls, line, filePath, T0);

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
                System.IO.Path.GetTempPath(), "pudding-d4-replace-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, System.IO.Path.Combine(root, "db", "code-index.db"));
        }

        /// <summary>给某个路径的 manifest 写入注入失败（验证整批回滚）。</summary>
        public async Task FailManifestWriteAsync(string filePath)
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
                CREATE TRIGGER IF NOT EXISTS d4_fail_manifest_write
                BEFORE INSERT ON CodeSourceManifest
                WHEN NEW.FilePath = '{escaped}'
                BEGIN
                    SELECT RAISE(ABORT, 'D4 injected manifest failure');
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
