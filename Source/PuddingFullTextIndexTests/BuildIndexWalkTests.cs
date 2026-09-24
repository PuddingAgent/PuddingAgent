using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// ADR-089 U4-6：索引构建的目录遍历改造 —— 单次遍历（旧实现按 77 个扩展名各遍历一次目录树）
/// 与噪声目录剪枝。
/// <para>
/// 这些用例锁定的是<b>行为等价性</b>：改造只改变「怎么走这棵树」，不改变「哪些文件最终被索引」。
/// 剪枝属于纯性能收益（落在被排除目录下的路径本来就会被 <see cref="FullTextIndexOptions.IsExcludedPath"/>
/// 拒绝），因此它没有语义可观测面，其证据是实跑计时（见 ADR-089 U4-6 与进度报告），
/// 而<b>不是</b>这里伪造一个会随机器抖动的时间断言。
/// </para>
/// </summary>
[TestClass]
public sealed class BuildIndexWalkTests
{
    private string _root = null!;
    private string _corpus = null!;
    private LuceneSearchEngine _engine = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-u46-" + Guid.NewGuid().ToString("N"));
        _corpus = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(_corpus);

        _engine = new LuceneSearchEngine(new FullTextIndexOptions
        {
            IndexRootDirectory = Path.Combine(_root, "index"),
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
    }

    [TestMethod]
    public async Task SingleWalk_IndexesEveryNestedFileExactlyOnce()
    {
        Write("a.cs", "class Alpha { const string Marker = \"WalkProbeAlpha\"; }");
        Write(Path.Combine("sub", "b.cs"), "class Beta { const string Marker = \"WalkProbeBeta\"; }");
        Write(Path.Combine("sub", "deeper", "c.cs"), "class Gamma { const string Marker = \"WalkProbeGamma\"; }");

        var result = await _engine.BuildIndexAsync(_corpus);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(3, result.IndexedFileCount, "每个文件只能被索引一次；计数膨胀即说明遍历/写入重复");
    }

    [TestMethod]
    public async Task NoiseDirectories_AreNotIndexed()
    {
        Write(Path.Combine("src", "Keep.cs"), "class Keep { const string Marker = \"WalkProbeKeep\"; }");
        Write(Path.Combine("bin", "Dropped.cs"), "class Dropped { const string Marker = \"WalkProbeBin\"; }");
        Write(Path.Combine("node_modules", "pkg", "index.js"), "// WalkProbeModules");
        Write(Path.Combine(".pudding", "cache", "note.md"), "WalkProbePudding");
        Write(Path.Combine("obj", "gen", "Gen.cs"), "class Gen { const string Marker = \"WalkProbeObj\"; }");

        var result = await _engine.BuildIndexAsync(_corpus);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.IndexedFileCount, "只有 src/Keep.cs 应当被索引");

        var search = await _engine.SearchAsync("WalkProbeBin", _corpus);
        Assert.AreEqual(0, search.TotalMatches, "bin 下的文件不得进入索引面");
    }

    [TestMethod]
    public async Task CallerPatterns_AreStillHonoured()
    {
        Write("a.cs", "class Alpha { const string Marker = \"WalkProbeAlpha\"; }");
        Write("b.md", "WalkProbeMarkdown");

        var result = await _engine.BuildIndexAsync(_corpus, "*.md");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.IndexedFileCount, "显式 pattern 仍须生效（只索引 .md）");

        var search = await _engine.SearchAsync("WalkProbeAlpha", _corpus);
        Assert.AreEqual(0, search.TotalMatches, ".cs 不在本次 pattern 内，不应被索引");
    }

    [TestMethod]
    public async Task EmptyAndOversizedFiles_AreSkipped()
    {
        Write("ok.cs", "class Ok { const string Marker = \"WalkProbeOk\"; }");
        Write("empty.cs", string.Empty);
        Write("huge.cs", new string('x', 4096) + " WalkProbeHuge");

        var engine = new LuceneSearchEngine(new FullTextIndexOptions
        {
            IndexRootDirectory = Path.Combine(_root, "index-small"),
            MaxFileSizeBytes = 1024,
        });

        try
        {
            var result = await engine.BuildIndexAsync(_corpus);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(1, result.IndexedFileCount, "空文件与超限文件都不应被索引");
        }
        finally
        {
            engine.Dispose();
        }
    }

    [TestMethod]
    public async Task ExcludedFileNames_AreSkipped()
    {
        Write("Keep.cs", "class Keep { const string Marker = \"WalkProbeKeep\"; }");
        Write("package-lock.json", "{ \"WalkProbeLock\": true }");

        var result = await _engine.BuildIndexAsync(_corpus);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.IndexedFileCount, "package-lock.json 在排除文件名清单内");

        var search = await _engine.SearchAsync("WalkProbeLock", _corpus);
        Assert.AreEqual(0, search.TotalMatches);
    }

    [TestMethod]
    public async Task Search_FindsTheNestedFile_AfterASingleWalkBuild()
    {
        Write(Path.Combine("src", "deep", "Target.cs"), "class Target { const string Marker = \"WalkProbeTarget\"; }");

        await _engine.BuildIndexAsync(_corpus);

        var search = await _engine.SearchAsync("WalkProbeTarget", _corpus);

        Assert.IsTrue(search.Success, search.Error);
        Assert.AreEqual(1, search.TotalMatches, "单次遍历后，深层文件仍须可被检索到（且只出现一次）");
        StringAssert.Contains(search.Matches[0].FilePath, "Target.cs");
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_corpus, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
