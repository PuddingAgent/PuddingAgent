using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A23：索引新鲜度探针（<see cref="IFullTextIndexFreshnessProbe"/>）的**三态**契约与真实往返。
/// <para>
/// 为什么必须测三态而不是只测「有索引时能读到时间」：2026-09-25 事故的教训是
/// 「读不出」被伪报成某个看似合理的事实（当时是「0 篇文档」）会让下游做出错误判断。
/// 本探针的对应风险是：把「stamp 缺失/损坏」伪报成「刚建过」（下游于是放心使用陈旧索引）
/// 或伪报成时间戳（凭空造事实）。用例 3 专门锁死这一点。
/// </para>
/// <para>
/// 用例 2 自带**对照组**：先断言 <see cref="FullTextIndexResult.Success"/> 且索引确实写入了文件 ——
/// 没有这个对照，「Available」可能只是「空索引也算建成」。
/// </para>
/// <para>
/// ⚠️ 索引根一律落在 <see cref="Path.GetTempPath"/> 之下（R8）：绝不写生产索引根 <c>D:\data\fulltext-index</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class FullTextIndexFreshnessProbeTests
{
    private string _root = null!;
    private string _corpus = null!;
    private string _indexRoot = null!;
    private LuceneSearchEngine _engine = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-a23-" + Guid.NewGuid().ToString("N"));
        _corpus = Path.Combine(_root, "corpus");
        _indexRoot = Path.Combine(_root, "index");
        Directory.CreateDirectory(_corpus);

        File.WriteAllText(Path.Combine(_corpus, "note.md"), "# freshness\n\nA23 probe corpus.\n");

        _engine = new LuceneSearchEngine(new FullTextIndexOptions
        {
            IndexRootDirectory = _indexRoot,
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
            // 临时目录清理失败不应把测试判红（与既有用例一致）。
        }
    }

    /// <summary>探针一律经**公共契约**调用（不是 internal 接缝），证明调用方能真的拿到它。</summary>
    private IFullTextIndexFreshnessProbe Probe => (IFullTextIndexFreshnessProbe)_engine;

    /// <summary>该 scope 的 live 索引目录（命名哈希单一真源，与引擎实现同源）。</summary>
    private string IndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(_indexRoot, _corpus);

    [TestMethod]
    public void ProbeFreshness_UnbuiltScope_ReportsMissing()
    {
        var readout = Probe.ProbeFreshness(_corpus);

        Assert.AreEqual(FullTextIndexFreshnessState.Missing, readout.State);
        Assert.IsNull(readout.LastIndexedAtUtc, "从未建索引时不得报出任何时间戳（那会是凭空造事实）。");
        Assert.IsNull(readout.PatternFingerprint);
        Assert.IsNull(readout.IndexDirectoryLastWriteUtc);
        Assert.IsNull(readout.StampAgeAt(DateTimeOffset.UtcNow));
        Assert.IsFalse(Directory.Exists(IndexDirectory),
            "探针必须是只读的：不得因为探测而创建索引目录。");
    }

    [TestMethod]
    public async Task ProbeFreshness_AfterBuild_ReportsAvailableStampAndDirectoryMtime()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var build = await _engine.BuildIndexAsync(_corpus, "*.md", CancellationToken.None);

        // 对照组：先把「索引真的建成了且确实含文件」钉死，否则 Available 可能只是「空索引也报建成」。
        Assert.IsTrue(build.Success, $"建索引必须成功：{build.Error}");
        Assert.IsTrue(build.IndexedFileCount > 0, "对照组失败：索引里 0 个文件时本用例的 Available 断言不成立。");

        var readout = Probe.ProbeFreshness(_corpus);

        Assert.AreEqual(FullTextIndexFreshnessState.Available, readout.State);
        Assert.IsNotNull(readout.LastIndexedAtUtc, "stamp 可读时必须给出上次索引扫描时刻。");
        Assert.IsTrue(readout.LastIndexedAtUtc!.Value >= before && readout.LastIndexedAtUtc.Value <= DateTimeOffset.UtcNow.AddMinutes(1),
            $"stamp 时刻应落在本次构建附近，实际 {readout.LastIndexedAtUtc:O}（Kind 错位会整体平移时区，本断言即是防线）。");
        Assert.AreEqual(readout.LastIndexedAtUtc.Value.Offset, TimeSpan.Zero, "上报的时间戳一律 UTC。");
        Assert.IsNotNull(readout.PatternFingerprint, "12 位模式指纹应随 stamp 一并上报（供判断『换过 patterns 没』）。");
        Assert.AreEqual(12, readout.PatternFingerprint!.Length);
        Assert.IsNotNull(readout.IndexDirectoryLastWriteUtc, "该 scope 自己的 live 索引目录 mtime 应可读（既有 per-scope 新鲜度口径）。");

        var age = readout.StampAgeAt(DateTimeOffset.UtcNow);
        Assert.IsNotNull(age);
        Assert.IsTrue(age!.Value >= TimeSpan.Zero && age.Value < TimeSpan.FromMinutes(5),
            $"刚建完索引的年龄应接近 0，实际 {age}");
    }

    [TestMethod]
    public async Task ProbeFreshness_StampMissing_ReportsUnreadableAndNeverFabricatesTime()
    {
        var build = await _engine.BuildIndexAsync(_corpus, "*.md", CancellationToken.None);
        Assert.IsTrue(build.Success, $"建索引必须成功：{build.Error}");

        var stampPath = Path.Combine(IndexDirectory, ".last_indexed");
        Assert.IsTrue(File.Exists(stampPath), "对照组失败：没有 stamp 文件则本用例无法证明『缺失会被区分』。");
        File.Delete(stampPath);

        var readout = Probe.ProbeFreshness(_corpus);

        // 三态的核心：目录在、stamp 读不出 ⇒ 必须如实上报「读不出」，而不是 Available、也不是任何时间戳。
        Assert.AreEqual(FullTextIndexFreshnessState.StampUnreadable, readout.State,
            "stamp 缺失时不得伪报 Available —— 那会让调用方以为索引是新鲜的。");
        Assert.IsNull(readout.LastIndexedAtUtc, "读不出时不得用目录 mtime 冒充 stamp 时刻（两个口径不可混用）。");
        Assert.IsNull(readout.PatternFingerprint);
        Assert.IsNull(readout.StampAgeAt(DateTimeOffset.UtcNow));
        Assert.IsNotNull(readout.IndexDirectoryLastWriteUtc,
            "目录仍存在 ⇒ 目录 mtime 这一独立事实应照常上报（它不依赖 stamp）。");
    }
}
