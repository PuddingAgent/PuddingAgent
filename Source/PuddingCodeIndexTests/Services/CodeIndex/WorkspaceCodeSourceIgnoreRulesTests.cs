using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// 忽略规则适配器门禁：名字级噪声名单 + 真实 .gitignore 忽略栈（唯一真源 = <c>PuddingPathFiltering</c>），
/// 并且**按仓库根缓存**忽略栈、按有效期重建（用户新写的 .gitignore 不重启也生效）。
/// </summary>
[TestClass]
public sealed class WorkspaceCodeSourceIgnoreRulesTests : IDisposable
{
    private string _root = null!;
    private DateTimeOffset _now = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-ignore-rules-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    private WorkspaceCodeSourceIgnoreRules Rules(TimeSpan? refresh = null) =>
        new(new MutableTimeProvider(() => _now), refresh);

    private void WriteGitIgnore(params string[] lines) =>
        File.WriteAllText(Path.Combine(_root, ".gitignore"), string.Join('\n', lines) + "\n");

    [TestMethod]
    public void NoiseDirectoriesAreIgnoredAtAnyDepth()
    {
        var rules = Rules();
        var nodeModules = Path.Combine(_root, "src", "node_modules");

        Assert.IsTrue(rules.IsIgnored(nodeModules, isDirectory: true));
        Assert.IsTrue(rules.IsIgnored(Path.Combine(nodeModules, "pkg", "index.js"), isDirectory: false));
    }

    [TestMethod]
    public void TheWorkspaceItsOwnNoiseNamedAncestorsDoNotExcludeIt()
    {
        // 仓库检出在 <c>…\Temp\…</c> / <c>…\build\…</c> 这类目录之下时，绝不能因此把整棵工作区判成噪声：
        // 名字级噪声必须**相对仓库根**判定（本仓库的测试根就在系统 Temp 下，正是这条真实风险）。
        var rules = Rules();
        File.WriteAllText(Path.Combine(_root, ".gitignore"), string.Empty);

        Assert.IsTrue(
            _root.Contains("Temp", StringComparison.OrdinalIgnoreCase),
            "前提：测试根本身位于一个「名字级噪声」目录之下");
        Assert.IsFalse(rules.IsIgnored(Path.Combine(_root, "src", "Service.cs"), isDirectory: false));
    }

    [TestMethod]
    public void GitIgnoredPathsAreIgnoredForFilesAndDirectories()
    {
        var rules = Rules();
        WriteGitIgnore("vendor/", "*.generated.cs");
        var vendor = Path.Combine(_root, "vendor");
        Directory.CreateDirectory(vendor);

        Assert.IsTrue(rules.IsIgnored(vendor, isDirectory: true), "被 .gitignore 忽略的目录整棵子树都不枚举");
        Assert.IsTrue(rules.IsIgnored(Path.Combine(vendor, "lib.cs"), isDirectory: false));
        Assert.IsTrue(rules.IsIgnored(Path.Combine(_root, "Model.generated.cs"), isDirectory: false));
    }

    [TestMethod]
    public void OrdinarySourceFilesAreNotIgnored()
    {
        var rules = Rules();
        WriteGitIgnore("vendor/");

        Assert.IsFalse(rules.IsIgnored(Path.Combine(_root, "src", "Service.cs"), isDirectory: false));
        Assert.IsFalse(rules.IsIgnored(Path.Combine(_root, "src"), isDirectory: true));
    }

    [TestMethod]
    public void TheIgnoreStackIsCachedPerRepositoryAndRefreshedAfterTheInterval()
    {
        var rules = Rules(refresh: TimeSpan.FromMinutes(5));
        WriteGitIgnore("first/");
        var second = Path.Combine(_root, "second");

        Assert.IsFalse(rules.IsIgnored(second, isDirectory: true));

        // 用户现在新增了一条忽略规则：缓存期内不重新读盘（同一仓库只读一次），过期后必须生效。
        File.AppendAllText(Path.Combine(_root, ".gitignore"), "second/\n");
        Assert.IsFalse(rules.IsIgnored(second, isDirectory: true), "缓存期内不重复读 .gitignore");

        _now = _now.AddMinutes(6);
        Assert.IsTrue(rules.IsIgnored(second, isDirectory: true), "过期必须重建，否则新规则永远不生效");
        Assert.AreEqual(1, rules.CachedRepositoryCount, "同一仓库根只缓存一份");
    }

    [TestMethod]
    public void BlankPathsAndInvalidIntervalsAreHandled()
    {
        var rules = Rules();

        Assert.IsFalse(rules.IsIgnored("   ", isDirectory: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceCodeSourceIgnoreRules(refreshInterval: TimeSpan.Zero));
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

    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;

        public MutableTimeProvider(Func<DateTimeOffset> now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now();
    }
}
