namespace PuddingCode.Abstractions;

/// <summary>归因聚合的总量口径。</summary>
public sealed record GoodputUsageTotals(
    int UsageRows,
    long PromptTokens,
    long CompletionTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    decimal PricedCost,
    int ZeroCostWithTokensRows)
{
    /// <summary>无记录时为空口径。</summary>
    public static GoodputUsageTotals Empty { get; } = new(0, 0, 0, 0, 0, 0m, 0);
}

/// <summary>单个 TraceId 的归因结果。<c>RecordedPromptTokens</c> 来自迭代结算记账，用于交叉核对。</summary>
public sealed record GoodputTraceAttribution(
    string TraceId,
    int Rounds,
    int IterationCount,
    int PendingIterations,
    long RecordedPromptTokens,
    long RecordedCompletionTokens,
    GoodputUsageTotals Totals,
    IReadOnlyList<string> ProviderModelIds)
{
    /// <summary>
    /// 归因结果与迭代结算记账的一致性三态：
    /// <c>agrees</c>（逐字一致）/ <c>mismatch</c>（该 TraceId 跨多个迭代或存在过度归因 ⇒ 必须可见，不得静默取信）/
    /// <c>not_comparable</c>（窗口内无用量行，或存在尚未结算的迭代 —— 未结算迭代的记账列必然为 0）。
    /// </summary>
    public string LedgerConsistency =>
        Totals.UsageRows == 0 || PendingIterations > 0
            ? "not_comparable"
            : Totals.PromptTokens == RecordedPromptTokens && Totals.CompletionTokens == RecordedCompletionTokens
                ? "agrees"
                : "mismatch";
}

/// <summary>
/// Goal 级归因报告。<see cref="SavingsClaimable"/> 只有在扫描窗口内**不存在**「非零 token 却零成本」的行、
/// 且**存在可归因行**时才为 true，即：0usage / 缺价 / 未知归因都不得被当成节省。
/// </summary>
public sealed record GoodputAttributionReport(
    string GoalRunId,
    int IterationCount,
    int IterationsWithoutUsage,
    int TraceCount,
    int ScannedUsageRows,
    int UnattributedScannedRows,
    GoodputUsageTotals Totals,
    IReadOnlyList<GoodputTraceAttribution> Traces)
{
    /// <summary>
    /// 是否可据本报告宣称节省。
    /// 存在零成本非零 token 行时必须为 false；且**没有任何可归因行时也必须为 false**
    /// （避免空洞真值把「缺数据」当成「节省」）。
    /// </summary>
    public bool SavingsClaimable => Totals.UsageRows > 0 && Totals.ZeroCostWithTokensRows == 0;
}

/// <summary>Goal 级归因读数（只读）。</summary>
public interface IGoodputAttributionService
{
    /// <summary>按 GoalRunId 读取归因报告；只读，不写入任何表。</summary>
    Task<GoodputAttributionReport> GetGoalReportAsync(
        string goalRunId,
        int maxScanRows = 20000,
        CancellationToken ct = default);
}
