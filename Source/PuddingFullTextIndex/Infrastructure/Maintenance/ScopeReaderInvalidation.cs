using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// **查询侧 reader 缓存失效**接缝（方案 §4.5：<c>commit 成功后显式 InvalidateScope</c>）。
/// <para>
/// 语义：把一个语料根对应的查询侧 reader / searcher 缓存条目移除并释放句柄，使后续查询无需重启
/// 即可看到最新 commit（实现见 <c>LuceneSearchEngine.InvalidateScope</c>）。
/// </para>
/// <para>
/// <b>为什么把它做成接缝而不是直接调 <c>LuceneSearchEngine</c></b>：
/// <list type="number">
/// <item><description><c>LuceneSearchEngine</c> 是 <c>sealed</c>、<c>InvalidateScope</c> 非虚 ⇒ 无法用替身观测
/// 「恰好调用 1 次 / 参数是 <c>changeSet.ScopeRoot</c>」，而调用计数是本片的一条硬不变量（L6）；</description></item>
/// <item><description>本片对 <c>LuceneSearchEngine.cs</c> 要求 <b>0 改动</b>：真实实现只是把调用转给它的
/// <c>internal InvalidateScope</c>，因此这个接缝是「统计能力」而不是「新行为」。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 参数是**语料根**（不是索引目录）：与既有调用方 <c>StagedFullTextIndexBuilder</c> 同口径
/// （它传的也是 <c>scope.RootPath</c>）。
/// </para>
/// <para>
/// ⚠️ 为何是 <c>public</c>（而不是 internal）：<see cref="LuceneFullTextIndexMaintenanceEngine"/> 的构造函数是
/// <c>public</c>（S5 组合根需要），而 C# 不允许 public 成员暴露 internal 类型（CS0051）。本组件在
/// <c>Infrastructure/Maintenance</c> 下本来就有一批 public 类型（引擎 / 选项 / 专用异常 / 报告），因此与既有风格一致。
/// </para>
/// </summary>
public interface IScopeReaderInvalidation
{
    /// <summary>失效某个语料根对应索引目录的 reader / searcher 缓存（幂等）。</summary>
    /// <param name="corpusRootPath">语料根目录（不是索引目录）。</param>
    void InvalidateScope(string corpusRootPath);
}

/// <summary>
/// 接缝的**真实实现**：把调用转给同一个 <c>LuceneSearchEngine</c> 实例的 <c>internal InvalidateScope</c>。
/// <para>被测路径与生产路径共用这一个实现（测试里另有一个只计数的替身，用于 L6 的调用计数断言）。</para>
/// </summary>
public sealed class SearchEngineScopeReaderInvalidation : IScopeReaderInvalidation
{
    private readonly LuceneSearchEngine _searchEngine;

    public SearchEngineScopeReaderInvalidation(LuceneSearchEngine searchEngine) =>
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));

    /// <inheritdoc />
    public void InvalidateScope(string corpusRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRootPath);
        _searchEngine.InvalidateScope(corpusRootPath);
    }
}
