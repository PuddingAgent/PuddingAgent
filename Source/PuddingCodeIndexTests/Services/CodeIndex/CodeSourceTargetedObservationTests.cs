using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D4 按路径观测门禁（2026-10-02）：提示驱动的批次**不得遍历整棵树**，而且它的结论必然不完整 ——
/// 因此永远不能得出「某路径已删除」（删除仍只能由周期性完整扫描确认）。
/// </summary>
[TestClass]
public sealed class CodeSourceTargetedObservationTests : IDisposable
{
    private const string WorkspaceId = "ws-targeted";
    private const string ScopeId = "scope-targeted";

    private string _root = null!;
    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-targeted-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CodeSourceScanService Service(ICodeSourceScanner scanner) =>
        new(scanner, _store, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeSourceScanService>.Instance);

    [TestMethod]
    public async Task AHintedRunObservesOnlyTheHintsAndNeverWalksTheTree()
    {
        var changed = WriteFile("src/A.cs", "class A { }");
        WriteFile("src/B.cs", "class B { }");

        var scanner = new FakeScanner { PathProbe = true };
        var run = await Service(scanner).RunAsync(
            WorkspaceId,
            ScopeId,
            _root,
            new CodeSourceScanOptions(WatcherHints: [changed], Targeted: true));

        Assert.AreEqual(0, scanner.FullScanCount, "提示驱动不得遍历整棵树");
        Assert.AreEqual(1, scanner.ProbeCount, "只应做一次按路径观测");
        CollectionAssert.AreEqual(new[] { changed }, scanner.LastProbedPaths!.ToArray());

        Assert.IsTrue(
            run.ChangeSet.Changes.Any(change => change.FilePath == changed),
            "被提示的文件必须进入变更集");
        Assert.IsFalse(run.ChangeSet.ScanComplete, "按路径观测必然不完整");
    }

    [TestMethod]
    public async Task ATargetedRunNeverConfirmsADeletion()
    {
        var file = WriteFile("src/A.cs", "class A { }");
        var scanner = new FakeScanner { PathProbe = true };

        // 先让「该文件已被记录」成立（manifest 由执行层写：校准本身只登记期望版本）。
        var info = new FileInfo(file);
        await _store.SaveSourceManifestAsync(
            WorkspaceId,
            ScopeId,
            [new CodeSourceEntry(
                file,
                new SourceFingerprint(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length, "seeded"),
                [new AppliedFileVersion("C#", "policy", "semantic", 1)])],
            []);

        // 文件消失 + 只给提示：不完整观测绝不能确认删除。
        File.Delete(file);
        var run = await Service(scanner).RunAsync(
            WorkspaceId,
            ScopeId,
            _root,
            new CodeSourceScanOptions(WatcherHints: [file], Targeted: true));

        Assert.IsFalse(run.ChangeSet.ScanComplete);
        Assert.AreEqual(
            0,
            run.ChangeSet.DeletedCount,
            "提示驱动的观测不完整 ⇒ 不得确认任何删除（删除留给周期性完整扫描）");
        Assert.IsTrue(
            (await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Manifest.ContainsKey(file),
            "旧记录必须保留，等完整扫描定论");
    }

    [TestMethod]
    public async Task WithoutTargetedTheServiceStillDoesAFullScan()
    {
        WriteFile("src/A.cs", "class A { }");
        WriteFile("src/B.cs", "class B { }");

        var scanner = new FakeScanner { PathProbe = true };
        var run = await Service(scanner).RunAsync(
            WorkspaceId,
            ScopeId,
            _root,
            new CodeSourceScanOptions(WatcherHints: [Path.Combine(_root, "src", "A.cs")]));

        Assert.AreEqual(1, scanner.FullScanCount, "周期性校准仍必须完整枚举");
        Assert.AreEqual(0, scanner.ProbeCount);
        Assert.IsTrue(run.ChangeSet.ScanComplete);
    }

    [TestMethod]
    public async Task WithoutTheProbeCapabilityAHintedRunFallsBackToAFullScan()
    {
        var changed = WriteFile("src/A.cs", "class A { }");

        // 只实现完整枚举的扫描器（没有按路径能力）：必须退回完整枚举，正确性优先于省读盘。
        var scanner = new FullScanOnlyScanner();
        var run = await Service(scanner).RunAsync(
            WorkspaceId,
            ScopeId,
            _root,
            new CodeSourceScanOptions(WatcherHints: [changed], Targeted: true));

        Assert.AreEqual(1, scanner.FullScanCount);
        Assert.IsFalse(run.ChangeSet.ScanComplete is false, "完整枚举仍然给出完整结论");
    }

    [TestMethod]
    public async Task TheRealScannerObservesOnlyTheGivenPaths()
    {
        var changed = WriteFile("src/A.cs", "class A { }");
        WriteFile("src/B.cs", "class B { }");
        WriteFile("other/C.cs", "class C { }");

        var scanner = new FileSystemCodeSourceScanner(new PermissiveIgnoreRules());
        var outcome = await scanner.ObserveAsync([changed]);

        Assert.IsFalse(outcome.Complete, "按路径观测永远不完整");
        Assert.IsTrue(outcome.RootUsable);
        CollectionAssert.AreEqual(new[] { changed }, outcome.Entries.Select(entry => entry.FilePath).ToArray());
        Assert.IsTrue(outcome.Entries[0].Length > 0, "元数据必须真实读到");
    }

    [TestMethod]
    public async Task TheRealScannerReportsAMissingHintedPathWithoutMetadata()
    {
        var missing = Path.Combine(_root, "src", "gone.cs");
        var scanner = new FileSystemCodeSourceScanner(new PermissiveIgnoreRules());

        var outcome = await scanner.ObserveAsync([missing]);

        Assert.IsFalse(outcome.Complete);
        Assert.HasCount(1, outcome.Entries);
        Assert.IsNull(outcome.Entries[0].LastWriteTimeUtc);
        Assert.IsNull(outcome.Entries[0].Length, "读不到就是 null，不得用别的值顶替");
    }

    [TestMethod]
    public async Task TheRealScannerExpandsADirectoryHintToItsSubtree()
    {
        WriteFile("src/nested/A.cs", "class A { }");
        WriteFile("src/nested/B.cs", "class B { }");
        var directory = Path.Combine(_root, "src", "nested");

        var scanner = new FileSystemCodeSourceScanner(new PermissiveIgnoreRules());
        var outcome = await scanner.ObserveAsync([directory]);

        Assert.HasCount(2, outcome.Entries, "目录提示要枚举子树（目录变更可能影响它下面的文件）");
        Assert.IsFalse(outcome.Complete);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp 目录清理是 best-effort
        }
    }

    /// <summary>可编程扫描器：是否能按路径观测、各走了几次，全部可断言。</summary>
    private sealed class FakeScanner : ICodeSourceScanner, ICodeSourcePathProbe
    {
        public bool PathProbe { get; init; }

        public int FullScanCount { get; private set; }

        public int ProbeCount { get; private set; }

        public IReadOnlyCollection<string>? LastProbedPaths { get; private set; }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            FullScanCount++;

            var entries = Directory.Exists(rootPath)
                ? Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        var info = new FileInfo(path);
                        return new CodeSourceDiskEntry(
                            path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
                    })
                    .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

            return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: true, null));
        }

        Task<CodeSourceScanOutcome> ICodeSourcePathProbe.ObserveAsync(
            IReadOnlyCollection<string> absolutePaths,
            CancellationToken cancellationToken)
        {
            if (!PathProbe)
                throw new InvalidOperationException("this double does not support path probing");

            ProbeCount++;
            LastProbedPaths = absolutePaths;

            var entries = absolutePaths
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return info.Exists
                        ? new CodeSourceDiskEntry(
                            path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length)
                        : new CodeSourceDiskEntry(path, null, null);
                })
                .ToArray();

            // 按路径观测永远不完整。
            return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: false, null));
        }
    }

    private sealed class PermissiveIgnoreRules : ICodeSourceIgnoreRules
    {
        public bool IsIgnored(string absolutePath, bool isDirectory) => false;
    }

    /// <summary>只实现完整枚举的扫描器（没有按路径观测能力）。</summary>
    private sealed class FullScanOnlyScanner : ICodeSourceScanner
    {
        public int FullScanCount { get; private set; }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            FullScanCount++;

            var entries = Directory.Exists(rootPath)
                ? Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        var info = new FileInfo(path);
                        return new CodeSourceDiskEntry(
                            path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
                    })
                    .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

            return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: true, null));
        }
    }
}
