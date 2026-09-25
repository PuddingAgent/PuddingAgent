namespace PuddingFullTextIndex.Contracts;

/// <summary>干跑（Plan）中被接受的 scope 及其估算值。</summary>
/// <param name="ScopeKey">规范化 scope 键。</param>
/// <param name="RootPath">规范化 scope 路径。</param>
/// <param name="FileCount">按索引口径清点到的可索引文件数。</param>
/// <param name="CorpusBytes">这些文件的字节总量。</param>
/// <param name="PredictedIndexBytes">预测索引体积 = <c>CorpusBytes × 系数</c>（系数见 <see cref="SupplyPlanResult.IndexSizeFactor"/>）。</param>
/// <param name="BudgetBytes">本 scope 适用的预算（字节）。</param>
/// <param name="WithinBudget">预测体积是否在预算内。⚠️ A1 只做报表，<b>不做</b>硬限（硬限属 A2）。</param>
public sealed record SupplyPlanScope(
    string ScopeKey,
    string RootPath,
    int FileCount,
    long CorpusBytes,
    long PredictedIndexBytes,
    long BudgetBytes,
    bool WithinBudget);

/// <summary>
/// 干跑结果（<c>PlanAsync</c>）：<b>零写入</b>——不建索引目录、不写租约、不写任何文件。
/// </summary>
/// <param name="Accepted">
/// 请求是否被完整接受：无任何拒绝项<b>且</b>至少有一个被接受的 scope。
/// （部分拒绝 ⇒ false，但 <see cref="AcceptedScopes"/> 仍如实列出可用部分，不隐藏。）
/// </param>
/// <param name="RejectedScopes">逐条拒绝（值 + 原因枚举 + 可读消息）。</param>
/// <param name="AcceptedScopes">逐 scope 估算。</param>
/// <param name="BudgetBytes">本次请求生效的预算（请求值优先，否则协调器默认值）。</param>
/// <param name="IndexSizeFactor">本次预测所用的「索引字节 / 语料字节」系数（口径来源见实现常量注释）。</param>
public sealed record SupplyPlanResult(
    bool Accepted,
    IReadOnlyList<SupplyScopeRejection> RejectedScopes,
    IReadOnlyList<SupplyPlanScope> AcceptedScopes,
    long BudgetBytes,
    double IndexSizeFactor);
