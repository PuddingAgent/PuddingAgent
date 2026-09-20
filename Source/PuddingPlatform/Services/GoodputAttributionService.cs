using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 归因解析。运行时写入 <c>TokenUsageEvents.SourceId</c> 的规范形态为
/// <c>{sessionId}:{traceId}:{round}</c>（见 <c>AgentExecution/AgentExecutionService.Buffered.cs</c> 与
/// <c>AgentExecution/AgentExecutionService.Streaming.cs</c> 的 <c>sourceId: $"{request.SessionId}:{trace.TraceId}:{round + 1}"</c>），
/// 另有带用途后缀的形态 <c>{sessionId}:{runIdentity}:{round}:warm-prefix</c>。
/// 中段 TraceId 可经 <c>execution_runs.trace_id</c> / <c>goal_iterations.trace_id</c> 桥接到 Goal 与 Task。
/// </summary>
public static class UsageAttribution
{
    /// <summary>无法解析出 TraceId 时的稳定原因码（不得被当成"零成本节省"）。</summary>
    public const string UnparsedReasonCode = "attribution_unparsed";

    /// <summary>解析 SourceId；失败时返回 <see cref="UsageAttributionKey.Parsed"/> = false 与稳定原因码。</summary>
    public static UsageAttributionKey Parse(string? sourceId, string? fallbackSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return new UsageAttributionKey(false, fallbackSessionId ?? string.Empty, string.Empty, 0, UnparsedReasonCode);
        }

        var parts = sourceId.Split(':');
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return new UsageAttributionKey(false, parts[0], string.Empty, 0, UnparsedReasonCode);
        }

        // 轮次取"中段之后第一个可解析为整数的段"，兼容 3 段与带用途后缀的形态。
        for (var i = 2; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out var round))
            {
                return new UsageAttributionKey(true, parts[0], parts[1], round, null);
            }
        }

        return new UsageAttributionKey(false, parts[0], parts[1], 0, UnparsedReasonCode);
    }
}

/// <summary>SourceId 的解析结果。<paramref name="TraceId"/> 是归因到 Goal / Task 的唯一中段标识。</summary>
public readonly record struct UsageAttributionKey(
    bool Parsed,
    string SessionId,
    string TraceId,
    int Round,
    string? ReasonCode);

/// <summary>价格状态码。用于把"真免费档"与"缺价格档案"分开，二者都不得计入节省。</summary>
public static class PricingStatusCodes
{
    /// <summary>有正价格档案，可按成本口径计入节省。</summary>
    public const string Priced = "priced";

    /// <summary>价格档案存在且为 0（真实免费档），不是节省。</summary>
    public const string Free = "free";

    /// <summary>无价格档案（缺档）⇒ 成本静默回落 0，必须标记且不得计入节省。</summary>
    public const string Unpriced = "unpriced";

    /// <summary>provider/model 缺失，无法判定价格状态。</summary>
    public const string UnknownProvider = "unknown_provider";
}

/// <summary>价格状态判定结果；<paramref name="CountsAsSaving"/> 为 true 仅当状态为 <see cref="PricingStatusCodes.Priced"/>。</summary>
public readonly record struct PricingClassification(string Status, string ReasonCode, bool CountsAsSaving);

/// <summary>价格状态判定（纯函数）。</summary>
public static class PricingClassifier
{
    /// <summary>按价格档案判定。<c>inputPricePer1M</c> / <c>outputPricePer1M</c> 均为 0 视为真实免费档。</summary>
    public static PricingClassification FromProfile(
        string? providerId,
        string? modelId,
        decimal inputPricePer1M,
        decimal outputPricePer1M)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            return new PricingClassification(PricingStatusCodes.UnknownProvider, "provider_or_model_missing", false);
        }

        if (inputPricePer1M <= 0m && outputPricePer1M <= 0m)
        {
            return new PricingClassification(PricingStatusCodes.Free, "zero_price_profile", false);
        }

        return new PricingClassification(PricingStatusCodes.Priced, "price_profile_present", true);
    }

    /// <summary>无价格档案时使用：成本会静默回落 0，必须标记为 unpriced 且不计入节省。</summary>
    public static PricingClassification Unpriced(string reasonCode = "price_profile_missing")
        => new(PricingStatusCodes.Unpriced, reasonCode, false);
}

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

/// <summary>单个 TraceId 的归因结果。</summary>
public sealed record GoodputTraceAttribution(
    string TraceId,
    int Rounds,
    GoodputUsageTotals Totals,
    IReadOnlyList<string> ProviderModelIds);

/// <summary>
/// Goal 级归因报告。<see cref="SavingsClaimable"/> 只有在扫描窗口内**不存在**"非零 token 却零成本"的行时才为 true，
/// 即：0usage / 缺价 / 未知归因都不得被当成节省。
/// </summary>
public sealed record GoodputAttributionReport(
    string GoalRunId,
    int IterationCount,
    int TraceCount,
    int ScannedUsageRows,
    int UnattributedScannedRows,
    GoodputUsageTotals Totals,
    IReadOnlyList<GoodputTraceAttribution> Traces)
{
    /// <summary>是否可据本报告宣称节省。存在零成本非零 token 行时必须为 false。</summary>
    public bool SavingsClaimable => Totals.ZeroCostWithTokensRows == 0;
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

/// <summary>
/// 归因聚合：把 <c>TokenUsageEvents</c> 经 SourceId 中段（TraceId）归因到 <c>goal_iterations.trace_id</c>，
/// 从而给出「每 verified task 的输入 / cache miss / 输出 / 费用」。只读实现，不改写入路径。
/// </summary>
public sealed class GoodputAttributionService(PlatformDbContext db) : IGoodputAttributionService
{
    private const int MaxScanRowsCeiling = 50000;

    public async Task<GoodputAttributionReport> GetGoalReportAsync(
        string goalRunId,
        int maxScanRows = 20000,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalRunId);
        var scan = Math.Clamp(maxScanRows, 1, MaxScanRowsCeiling);

        var iterations = await db.GoalIterations.AsNoTracking()
            .Where(i => i.GoalRunId == goalRunId)
            .ToListAsync(ct);

        var traceIds = iterations
            .Select(i => i.TraceId)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);

        // SQLite 无法翻译 DateTimeOffset 排序；TokenUsageEvents 是追加式账本，Id 单调 ⇒ 用 Id 降序取窗口。
        var events = await db.TokenUsageEvents.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(scan)
            .ToListAsync(ct);

        var matched = new List<(int Round, string TraceId, TokenUsageEventEntity Event)>();
        var unattributed = 0;
        foreach (var ev in events)
        {
            var key = UsageAttribution.Parse(ev.SourceId, ev.SessionId);
            if (!key.Parsed || !traceIds.Contains(key.TraceId))
            {
                unattributed++;
                continue;
            }

            matched.Add((key.Round, key.TraceId, ev));
        }

        var traces = matched
            .GroupBy(m => m.TraceId, StringComparer.Ordinal)
            .Select(g => new GoodputTraceAttribution(
                TraceId: g.Key,
                Rounds: g.Select(m => m.Round).Distinct().Count(),
                Totals: Aggregate(g.Select(m => m.Event)),
                ProviderModelIds: BuildProviderModelIds(g.Select(m => m.Event))))
            .OrderBy(t => t.TraceId, StringComparer.Ordinal)
            .ToList();

        return new GoodputAttributionReport(
            GoalRunId: goalRunId,
            IterationCount: iterations.Count,
            TraceCount: traceIds.Count,
            ScannedUsageRows: events.Count,
            UnattributedScannedRows: unattributed,
            Totals: Aggregate(matched.Select(m => m.Event)),
            Traces: traces);
    }

    private static GoodputUsageTotals Aggregate(IEnumerable<TokenUsageEventEntity> events)
    {
        var rows = 0;
        long prompt = 0, completion = 0, hit = 0, miss = 0;
        var cost = 0m;
        var zeroCostWithTokens = 0;

        foreach (var ev in events)
        {
            rows++;
            prompt += ev.PromptTokens;
            completion += ev.CompletionTokens;
            hit += ev.CacheHitTokens;
            miss += ev.CacheMissTokens;

            var eventCost = ev.TotalCost;
            if (eventCost > 0m)
            {
                cost += eventCost;
            }
            else if (ev.PromptTokens + ev.CompletionTokens > 0)
            {
                // 有 token 却零成本：可能是真实免费档，也可能是缺价格档案 ⇒ 一律不得计入节省。
                zeroCostWithTokens++;
            }
        }

        return rows == 0
            ? GoodputUsageTotals.Empty
            : new GoodputUsageTotals(rows, prompt, completion, hit, miss, cost, zeroCostWithTokens);
    }

    private static IReadOnlyList<string> BuildProviderModelIds(IEnumerable<TokenUsageEventEntity> events) => events
        .Select(e => string.IsNullOrWhiteSpace(e.ProviderId)
            ? (e.ModelId ?? string.Empty)
            : (string.IsNullOrWhiteSpace(e.ModelId) ? e.ProviderId : $"{e.ProviderId}/{e.ModelId}"))
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();
}
