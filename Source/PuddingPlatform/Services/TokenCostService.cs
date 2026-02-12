using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Token 成本聚合查询服务。
/// 提供 per-turn、per-tool、daily 维度的成本分析，用于 admin 页面和 agent 诊断。
/// </summary>
public sealed class TokenCostService(PlatformDbContext db)
{
    /// <summary>
    /// 按轮次聚合：返回指定 session 每轮 LLM 调用的成本明细。
    /// </summary>
    public async Task<List<TurnCostRow>> GetTurnCostsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        return await db.TokenUsageEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.TurnRound != null)
            .OrderBy(e => e.TurnRound)
            .Select(e => new TurnCostRow(
                TurnRound: e.TurnRound!.Value,
                ModelId: e.ModelId ?? "unknown",
                PromptTokens: e.PromptTokens,
                CacheHitTokens: e.CacheHitTokens,
                CacheMissTokens: e.CacheMissTokens,
                CacheHitRate: e.CacheHitRate,
                CompletionTokens: e.CompletionTokens,
                ToolNames: e.ToolNames,
                InputCost: e.InputCost,
                CacheHitCost: e.CacheHitCost,
                OutputCost: e.OutputCost,
                TotalCost: e.TotalCost,
                OccurredAtUtc: e.OccurredAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 按工具聚合：统计各工具的累计 token 消耗。
    /// </summary>
    public async Task<List<ToolCostRow>> GetToolCostsAsync(
        string? workspaceId = null,
        string? providerId = null,
        string? modelId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var query = db.TokenUsageEvents.AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.ToolNames));

        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(e => e.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(e => e.ProviderId == providerId);
        if (!string.IsNullOrWhiteSpace(modelId))
            query = query.Where(e => e.ModelId == modelId);
        if (from.HasValue)
            query = query.Where(e => e.OccurredAtUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.OccurredAtUtc <= to.Value);

        var events = await query
            .Select(e => new { e.ToolNames, e.TotalCost, e.CacheHitTokens, e.CacheMissTokens, e.CompletionTokens })
            .ToListAsync(ct);

        // 在客户端拆分逗号分隔的工具名并聚合
        var toolMap = new Dictionary<string, ToolCostAccumulator>();
        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.ToolNames)) continue;
            foreach (var toolName in ev.ToolNames.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = toolName.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                if (!toolMap.TryGetValue(trimmed, out var acc))
                {
                    acc = new();
                    toolMap[trimmed] = acc;
                }
                acc.CallCount++;
                acc.TotalCost += ev.TotalCost;
                acc.CacheHitTokens += ev.CacheHitTokens;
                acc.CacheMissTokens += ev.CacheMissTokens;
                acc.CompletionTokens += ev.CompletionTokens;
            }
        }

        return toolMap
            .Select(kv => new ToolCostRow(
                ToolName: kv.Key,
                CallCount: kv.Value.CallCount,
                CacheHitTokens: kv.Value.CacheHitTokens,
                CacheMissTokens: kv.Value.CacheMissTokens,
                CompletionTokens: kv.Value.CompletionTokens,
                TotalCost: kv.Value.TotalCost))
            .OrderByDescending(r => r.TotalCost)
            .ToList();
    }

    /// <summary>
    /// 按日聚合：返回指定日期的成本汇总（与 DeepSeek 后台对比）。
    /// </summary>
    public async Task<DailyCostSummary> GetDailySummaryAsync(
        DateTimeOffset date,
        CancellationToken ct = default)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        var events = await db.TokenUsageEvents.AsNoTracking()
            .Where(e => e.OccurredAtUtc >= dayStart && e.OccurredAtUtc < dayEnd)
            .Select(e => new
            {
                e.ProviderId,
                e.ModelId,
                e.CacheHitTokens,
                e.CacheMissTokens,
                e.CompletionTokens,
                e.InputCost,
                e.CacheHitCost,
                e.OutputCost,
                e.TotalCost,
            })
            .ToListAsync(ct);

        var providerGroups = events
            .GroupBy(e => (ProviderId: e.ProviderId ?? "unknown", ModelId: e.ModelId ?? "unknown"))
            .Select(g => new ProviderDailyRow(
                ProviderId: g.Key.ProviderId,
                ModelId: g.Key.ModelId,
                RequestCount: g.Count(),
                CacheHitTokens: g.Sum(e => e.CacheHitTokens),
                CacheMissTokens: g.Sum(e => e.CacheMissTokens),
                CompletionTokens: g.Sum(e => e.CompletionTokens),
                InputCost: Math.Round(g.Sum(e => e.InputCost), 6),
                CacheHitCost: Math.Round(g.Sum(e => e.CacheHitCost), 6),
                OutputCost: Math.Round(g.Sum(e => e.OutputCost), 6),
                TotalCost: Math.Round(g.Sum(e => e.TotalCost), 6)))
            .ToList();

        return new DailyCostSummary(
            Date: date.ToString("yyyy-MM-dd"),
            TotalCacheHitTokens: events.Sum(e => e.CacheHitTokens),
            TotalCacheMissTokens: events.Sum(e => e.CacheMissTokens),
            TotalCompletionTokens: events.Sum(e => e.CompletionTokens),
            TotalRequests: events.Count,
            InputCost: Math.Round(events.Sum(e => e.InputCost), 6),
            CacheHitCost: Math.Round(events.Sum(e => e.CacheHitCost), 6),
            OutputCost: Math.Round(events.Sum(e => e.OutputCost), 6),
            TotalCost: Math.Round(events.Sum(e => e.TotalCost), 6),
            ByProvider: providerGroups);
    }

    private class ToolCostAccumulator
    {
        public int CallCount;
        public decimal TotalCost;
        public long CacheHitTokens;
        public long CacheMissTokens;
        public long CompletionTokens;
    }
}

public sealed record TurnCostRow(
    int TurnRound,
    string ModelId,
    long PromptTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    double? CacheHitRate,
    long CompletionTokens,
    string? ToolNames,
    decimal InputCost,
    decimal CacheHitCost,
    decimal OutputCost,
    decimal TotalCost,
    DateTimeOffset OccurredAtUtc);

public sealed record ToolCostRow(
    string ToolName,
    int CallCount,
    long CacheHitTokens,
    long CacheMissTokens,
    long CompletionTokens,
    decimal TotalCost);

public sealed record DailyCostSummary(
    string Date,
    long TotalCacheHitTokens,
    long TotalCacheMissTokens,
    long TotalCompletionTokens,
    int TotalRequests,
    decimal InputCost,
    decimal CacheHitCost,
    decimal OutputCost,
    decimal TotalCost,
    IReadOnlyList<ProviderDailyRow> ByProvider);

public sealed record ProviderDailyRow(
    string ProviderId,
    string ModelId,
    int RequestCount,
    long CacheHitTokens,
    long CacheMissTokens,
    long CompletionTokens,
    decimal InputCost,
    decimal CacheHitCost,
    decimal OutputCost,
    decimal TotalCost);
