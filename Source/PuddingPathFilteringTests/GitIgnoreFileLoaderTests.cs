namespace PuddingPathFilteringTests;

/// <summary>
/// <see cref="GitIgnoreFileLoader"/> 是叶子中唯一接触文件系统的类型；这里用临时目录验证
/// 「嵌套 .gitignore 优先 / 噪声目录不进入 / 缺失根不抛异常」。
/// </summary>
[TestClass]
public sealed class GitIgnoreFileLoaderTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "u4-4-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [TestMethod]
    public void LoadsRootAndNestedGitIgnoreWithTheNestedOneWinning()
    {
        Write(".gitignore", "*.log\n");
        Write("sub/.gitignore", "!keep.log\n");
        Write("sub/keep.log", "x");
        Write("other.log", "x");

        var stack = GitIgnoreFileLoader.Load(_root);

        Assert.AreEqual(2, stack.Count);
        Assert.IsTrue(stack.IsIgnored("other.log", isDirectory: false));
        Assert.IsFalse(stack.IsIgnored("sub/keep.log", isDirectory: false),
            "the nested .gitignore must override the root one");
    }

    [TestMethod]
    public void NoiseDirectoriesAreNotDescendedInto()
    {
        Write(".gitignore", "*.log\n");
        // 噪声目录里的 .gitignore 不会被读入（其规则只可能命中自身子树，而该子树已被名字级规则排除）
        Write("node_modules/pkg/.gitignore", "!keep.log\n");

        var (stack, sources) = GitIgnoreFileLoader.LoadWithSources(_root);

        Assert.AreEqual(1, stack.Count);
        Assert.HasCount(1, sources);
        Assert.AreEqual(".gitignore", sources[0]);
    }

    [TestMethod]
    public void MissingRootYieldsEmptyStack()
    {
        var stack = GitIgnoreFileLoader.Load(Path.Combine(_root, "does-not-exist"));

        Assert.AreEqual(0, stack.Count);
        Assert.IsFalse(stack.IsIgnored("a/b.cs", isDirectory: false));
    }

    [TestMethod]
    public void RealRepositorySnapshotLoadsItsTwoGitIgnoreFiles()
    {
        // 冻结快照（根 + Source/PuddingPlatformAdmin）与生产载入路径给出同一棵规则树。
        var root = OracleFixture.ReadPatternSource("real-workspace-root.gitignore.txt");
        var nested = OracleFixture.ReadPatternSource("real-workspace-PuddingPlatformAdmin.gitignore.txt");
        Write(".gitignore", root);
        Write("Source/PuddingPlatformAdmin/.gitignore", nested);

        var (stack, sources) = GitIgnoreFileLoader.LoadWithSources(_root);

        Assert.HasCount(2, sources);
        Assert.IsGreaterThan(200, stack.Count, "the VisualStudio template .gitignore has hundreds of effective rules");
        Assert.IsTrue(stack.IsIgnored("Source/PuddingRuntime/obj/x.cs", isDirectory: false));
        Assert.IsFalse(stack.IsIgnored("Source/PuddingRuntime/Services/X.cs", isDirectory: false));
    }
}
