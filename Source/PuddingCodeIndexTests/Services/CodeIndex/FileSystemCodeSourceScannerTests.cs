using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D2 三源之一的「元数据扫描」门禁：只读元数据、目录被忽略即整棵子树不枚举、
/// 子树失败/触顶让本轮**不完整**（从而绝不被当成删除依据）、stat 读不到就是 null。
/// </summary>
[TestClass]
public sealed class FileSystemCodeSourceScannerTests
{
    private static FileSystemCodeSourceScanner Scanner(
        Func<string, bool, bool>? ignore = null,
        int maxEntries = FileSystemCodeSourceScanner.DefaultMaxEntries) =>
        new(new DelegateIgnoreRules(ignore ?? ((_, _) => false)), maxEntries);

    private static CodeSourceScanOutcome Scan(string root, Func<string, bool, bool>? ignore = null, int maxEntries = FileSystemCodeSourceScanner.DefaultMaxEntries) =>
        Scanner(ignore, maxEntries).ScanAsync(root).GetAwaiter().GetResult();

    [TestMethod]
    public void MissingRoot_IsReportedAsUnusableAndIncomplete()
    {
        using var tree = TempTree.Create();

        var outcome = Scan(System.IO.Path.Combine(tree.Root, "does-not-exist"));

        Assert.IsFalse(outcome.RootUsable);
        Assert.IsFalse(outcome.Complete);
        Assert.IsEmpty(outcome.Entries);
        Assert.AreEqual(CodeSourceScanReasons.RootUnusable, outcome.IncompleteReason);
    }

    [TestMethod]
    public void EmptyRootPath_IsReportedAsUnusable()
    {
        var outcome = Scan("   ");

        Assert.IsFalse(outcome.RootUsable);
        Assert.AreEqual(CodeSourceScanReasons.RootPathMissing, outcome.IncompleteReason);
    }

    [TestMethod]
    public void Scan_EnumeratesNestedFilesWithMetadataInDeterministicOrder()
    {
        using var tree = TempTree.Create();
        tree.Write("src/b.cs", "b");
        tree.Write("src/a.cs", "aa");
        tree.Write("README.md", "#");

        var outcome = Scan(tree.Root);

        Assert.IsTrue(outcome.RootUsable);
        Assert.IsTrue(outcome.Complete);
        Assert.IsNull(outcome.IncompleteReason);
        Assert.HasCount(3, outcome.Entries);

        var expected = new[]
        {
            tree.Path("README.md"),
            tree.Path("src/a.cs"),
            tree.Path("src/b.cs"),
        }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        CollectionAssert.AreEqual(
            expected,
            outcome.Entries.Select(entry => entry.FilePath).ToArray(),
            "枚举顺序必须确定（按组件路径身份排序）");

        var a = outcome.Entries.Single(entry => entry.FilePath.EndsWith("a.cs", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(a.LastWriteTimeUtc);
        Assert.AreEqual(2, a.Length);
    }

    [TestMethod]
    public void IgnoredDirectory_IsPrunedEntirely()
    {
        using var tree = TempTree.Create();
        tree.Write("src/a.cs", "a");
        tree.Write("node_modules/pkg/index.js", "x");
        tree.Write("bin/Debug/out.dll", "x");

        var outcome = Scan(tree.Root, (path, isDirectory) =>
            isDirectory
            && (path.EndsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("bin", StringComparison.OrdinalIgnoreCase)));

        Assert.HasCount(1, outcome.Entries);
        Assert.IsTrue(
            outcome.Entries[0].FilePath.EndsWith("a.cs", StringComparison.OrdinalIgnoreCase),
            "被忽略目录的整棵子树不得进入候选");
        Assert.IsTrue(outcome.Complete);
    }

    [TestMethod]
    public void IgnoredFile_IsExcludedButItsSiblingsAreKept()
    {
        using var tree = TempTree.Create();
        tree.Write("src/a.cs", "a");
        tree.Write("src/notes.md", "notes");

        var outcome = Scan(tree.Root, (path, isDirectory) =>
            !isDirectory && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

        Assert.HasCount(1, outcome.Entries);
        Assert.IsTrue(outcome.Entries[0].FilePath.EndsWith("a.cs", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EntryLimit_MakesTheRoundIncompleteInsteadOfSilentlyTruncating()
    {
        using var tree = TempTree.Create();
        for (var index = 0; index < 5; index++)
            tree.Write($"src/f{index}.cs", "x");

        var outcome = Scan(tree.Root, maxEntries: 2);

        Assert.IsTrue(outcome.RootUsable);
        Assert.IsFalse(outcome.Complete, "触顶必须报不完整，否则未枚举的路径会被当成删除");
        Assert.AreEqual(CodeSourceScanReasons.EntryLimitReached, outcome.IncompleteReason);
        Assert.HasCount(2, outcome.Entries);
    }

    [TestMethod]
    public void IgnoreRuleFailure_FallsBackToKeepingThePath()
    {
        using var tree = TempTree.Create();
        tree.Write("src/a.cs", "a");

        var outcome = Scan(tree.Root, (_, _) => throw new InvalidOperationException("rules broken"));

        Assert.IsTrue(outcome.Complete);
        Assert.HasCount(1, outcome.Entries, "规则出错时宁可多一个候选，也不能漏掉文件");
    }

    [TestMethod]
    public void Scanner_RejectsAnInvalidEntryLimit()
    {
        using var tree = TempTree.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileSystemCodeSourceScanner(new DelegateIgnoreRules((_, _) => false), maxEntries: 0));
    }

    /// <summary>测试用忽略规则适配器。</summary>
    private sealed class DelegateIgnoreRules : ICodeSourceIgnoreRules
    {
        private readonly Func<string, bool, bool> _predicate;

        public DelegateIgnoreRules(Func<string, bool, bool> predicate) => _predicate = predicate;

        public bool IsIgnored(string absolutePath, bool isDirectory) => _predicate(absolutePath, isDirectory);
    }

    /// <summary>临时目录树夹具。</summary>
    private sealed class TempTree : IDisposable
    {
        private TempTree(string root) => Root = root;

        public string Root { get; }

        public static TempTree Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pudding-d2-scan-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempTree(root);
        }

        public string Path(string relativePath) =>
            System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relativePath, string content)
        {
            var path = Path(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
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
