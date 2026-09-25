using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>引擎替身：只记录转发参数、返回固定结果 —— <b>不触</b>真实 Lucene。</summary>
internal sealed class StubFullTextSearchEngine : IFullTextSearchEngine
{
    private readonly FullTextIndexResult _result;

    internal StubFullTextSearchEngine(FullTextIndexResult result) => _result = result;

    internal List<(string Directory, string? Patterns)> BuildCalls { get; } = new();

    public bool HasIndex(string directoryPath) => false;

    public Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default,
        FullTextSearchScope? scope = null) =>
        Task.FromResult(new FullTextSearchResult(true, Array.Empty<FullTextSearchMatch>(), null, 0, 0));

    public Task<FullTextIndexResult> BuildIndexAsync(string directoryPath, string? filePatterns = null, CancellationToken ct = default)
    {
        BuildCalls.Add((directoryPath, filePatterns));
        return Task.FromResult(_result);
    }

    public bool RemoveIndex(string directoryPath) => false;
}

/// <summary>
/// 薄适配器（<see cref="FullTextSearchEngineIndexBuilder"/>）的转发/度量映射证据：
/// 用替身引擎验证「scope 路径原样转发 + 不传 pattern（走默认白名单）+ 度量字段一一对齐」。
/// </summary>
[TestClass]
public sealed class FullTextSearchEngineIndexBuilderTests
{
    [TestMethod]
    public async Task Adapter_Forwards_The_Scope_And_Maps_Engine_Metrics()
    {
        var engine = new StubFullTextSearchEngine(new FullTextIndexResult(true, 42, 12345, 678, null));
        var adapter = new FullTextSearchEngineIndexBuilder(engine);

        var result = await adapter.BuildAsync(new SupplyScope(@"c:\corpus", @"C:\Corpus"));

        Assert.AreEqual(1, engine.BuildCalls.Count);
        Assert.AreEqual(@"C:\Corpus", engine.BuildCalls[0].Directory, "必须转发规范化后的 scope 路径");
        Assert.IsNull(engine.BuildCalls[0].Patterns, "不得传 pattern ⇒ 走引擎默认扩展名白名单");
        Assert.IsTrue(result.Success);
        Assert.AreEqual(42, result.IndexedFileCount);
        Assert.AreEqual(12345L, result.TotalBytes);
        Assert.AreEqual(678L, result.ElapsedMs);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task Adapter_Propagates_Failure_And_The_Engine_Error_Text()
    {
        var engine = new StubFullTextSearchEngine(new FullTextIndexResult(false, 0, 0, 3, "engine-error"));
        var adapter = new FullTextSearchEngineIndexBuilder(engine);

        var result = await adapter.BuildAsync(new SupplyScope(@"c:\x", @"C:\x"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("engine-error", result.Error);
        Assert.AreEqual(3L, result.ElapsedMs);
    }
}
