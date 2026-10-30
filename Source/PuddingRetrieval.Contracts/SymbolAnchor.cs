namespace PuddingRetrieval.Contracts;

/// <summary>
/// 已解析的符号锚点（设计 §8.2 的 <c>workspace_search(symbol_ref="…", intent="callers")</c>）。
/// <para>
/// 语义边界：调用方<b>已经</b>把 <c>symbol_ref</c> 解析成唯一的 (项目, 符号) 身份，
/// 并且已经通过 FRESH 判定；引擎<b>不得</b>再用名字去猜目标。
/// </para>
/// <para>
/// <b>为什么不带查询词</b>：名字是多对一的（同名类、重载、跨项目同名），
/// "按名字找目标"本身就是关系面检索最容易出错的一步 —— 锚点的全部意义就是绕过它。
/// 所以本类型<b>只有身份，没有名字</b>：留着名字会诱使实现退回名字匹配。
/// </para>
/// <para>
/// <b>为什么只有两个字段</b>：引擎按 <see cref="ProjectId"/> + <see cref="SymbolId"/>
/// 就能直接取回符号记录（名称、行号、文件全部从记录里来），多存一份就是多一个会过期的副本。
/// 行号与 content_hash 属于 ref 的职责，不进请求：请求只表达"对谁做"，不表达"在第几行"。
/// </para>
/// </summary>
public sealed record SymbolAnchor
{
    /// <summary>构造锚点。</summary>
    /// <param name="projectId">符号所属项目标识（取自 ref 的 <c>p</c>）。</param>
    /// <param name="symbolId">符号标识（取自 ref 的 <c>i</c>）。</param>
    public SymbolAnchor(string projectId, string symbolId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("锚点必须给出项目标识（空项目无法定位符号）。", nameof(projectId));

        if (string.IsNullOrWhiteSpace(symbolId))
            throw new ArgumentException("锚点必须给出符号标识。", nameof(symbolId));

        ProjectId = projectId.Trim();
        SymbolId = symbolId.Trim();
    }

    /// <summary>符号所属项目标识。</summary>
    public string ProjectId { get; }

    /// <summary>符号标识。</summary>
    public string SymbolId { get; }

    /// <summary>诊断用短描述（不含行号等易过期信息）。</summary>
    public override string ToString() => $"{ProjectId}/{SymbolId}";
}
