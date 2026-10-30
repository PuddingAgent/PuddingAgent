using PuddingFullTextIndex;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// R1-05 门禁：查询持有 reader 期间发生缓存失效 / 刷新，不得抛
/// <c>ObjectDisposedException</c>、不得返回错误空结果、不得泄漏。
/// <para>
/// 用 <see cref="LuceneSearchEngine.AfterSearcherAcquired"/> 把「拿到 searcher 之后、真正查询之前」
/// 这个时序固定下来 —— 不靠 sleep/计时去赌并发窗口。
/// </para>
/// </summary>
[TestClass]
public sealed class ReaderLifecycleTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pfi-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }

    private async Task<LuceneSearchEngine> BuildAsync()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "needle");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "needle");

        var engine = new LuceneSearchEngine(new FullTextIndexOptions());
        var built = await engine.BuildIndexAsync(_root);
        Assert.IsTrue(built.Success, built.Error);

        // 预热：让 reader 进入缓存，使「失效」与「正在查询的 searcher」指向同一实例。
        var warm = await engine.SearchAsync("needle", _root);
        Assert.IsTrue(warm.Success, warm.Error);

        return engine;
    }

    [TestMethod]
    public async Task InvalidateDuringQuery_DoesNotBreakTheInFlightSearch()
    {
        var engine = await BuildAsync();

        engine.AfterSearcherAcquired = _ => engine.InvalidateScope(_root);

        var result = await engine.SearchAsync("needle", _root);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(0, result.Matches.Count, "失效不得让正在进行的查询变成空结果。");
    }

    /// <summary>缓存被换掉之后，下一次查询仍必须给出正确结果（不得留下已释放的 searcher）。</summary>
    [TestMethod]
    public async Task QueryAfterInvalidate_ReturnsFreshResults()
    {
        var engine = await BuildAsync();
        engine.InvalidateScope(_root);

        var result = await engine.SearchAsync("needle", _root);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(0, result.Matches.Count);
    }
}
