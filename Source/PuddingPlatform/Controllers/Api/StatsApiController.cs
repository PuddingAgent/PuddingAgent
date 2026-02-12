using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Token 使用统计 API — 聚合查询 + 明细事件 + 数据重建。
/// ADR-018：上下文缓存可观测性体系。ADR-043：缓存统计闭环。
/// </summary>
[Authorize]
[ApiController]
[Route("api/stats")]
public class StatsApiController(
    PlatformDbContext db,
    TokenUsageRebuildService rebuildService,
    ILlmConfigService llmConfigService) : ControllerBase
{
    /// <summary>
    /// GET /api/stats/tokens/monthly?yearMonth=2026-05&providerId=&modelId=
    /// 返回指定月份的 Token 用量统计，按 Provider → Model 二级分组。
    /// yearMonth 格式：yyyy-MM，默认当月。
    /// </summary>
    [HttpGet("tokens/monthly")]
    public async Task<IActionResult> GetMonthlyTokenStats(
        [FromQuery] string? yearMonth = null,
        [FromQuery] string? providerId = null,
        [FromQuery] string? modelId = null,
        CancellationToken ct = default)
    {
        yearMonth ??= DateTimeOffset.UtcNow.ToString("yyyy-MM");

        var query = db.TokenUsageStats.AsNoTracking()
            .Where(s => s.YearMonth == yearMonth);

        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(s => s.ProviderId == providerId);

        if (!string.IsNullOrWhiteSpace(modelId))
            query = query.Where(s => s.ModelId == modelId);

        var stats = await query
            .OrderBy(s => s.ProviderId)
            .ThenBy(s => s.ModelId)
            .ToListAsync(ct);

        if (stats.Count == 0)
        {
            return Ok(new
            {
                yearMonth,
                totalPromptTokens = 0L,
                totalCompletionTokens = 0L,
                totalCacheHitTokens = 0L,
                totalCacheMissTokens = 0L,
                cacheHitRate = 0.0,
                inputCost = 0m,
                cacheHitCost = 0m,
                outputCost = 0m,
                totalCost = 0m,
                totalRequests = 0L,
                byProvider = Array.Empty<object>(),
            });
        }

        // 加载价格配置（用于费用计算）
        var priceRows = llmConfigService.GetAllModels();

        var priceMap = priceRows
            .GroupBy(m => new { m.ProviderId, m.ModelId })
            .ToDictionary(
                g => (g.Key.ProviderId, g.Key.ModelId),
                g => new TokenPrice(
                    g.First().InputPricePer1MTokens,
                    g.First().OutputPricePer1MTokens,
                    g.First().CacheHitPricePer1MTokens));

        var unambiguousPriceMap = priceRows
            .GroupBy(m => m.ModelId)
            .Where(g => g.Count() == 1)
            .ToDictionary(
                g => g.Key,
                g => new TokenPrice(
                    g.First().InputPricePer1MTokens,
                    g.First().OutputPricePer1MTokens,
                    g.First().CacheHitPricePer1MTokens));

        var eventCostRows = await ApplyTokenEventFilters(
                db.TokenUsageEvents.AsNoTracking(),
                providerId,
                modelId)
            .Where(e => e.YearMonth == yearMonth)
            .GroupBy(e => new
            {
                ProviderId = e.ProviderId ?? "unknown",
                ModelId = e.ModelId ?? "unknown",
            })
            .Select(g => new TokenCostRow(
                g.Key.ProviderId,
                g.Key.ModelId,
                Math.Round(g.Sum(e => e.InputCost), 6),
                Math.Round(g.Sum(e => e.CacheHitCost), 6),
                Math.Round(g.Sum(e => e.OutputCost), 6)))
            .ToListAsync(ct);

        var eventCostMap = eventCostRows.ToDictionary(
            r => (r.ProviderId, r.ModelId),
            r => new TokenCostBreakdown(r.InputCost, r.CacheHitCost, r.OutputCost));

        // ── 补充未使用的 Provider/Model 零值行 ──
        var allModels = llmConfigService.GetAllModels();
        var existingKeys = new HashSet<(string, string)>();
        foreach (var s in stats)
            existingKeys.Add((s.ProviderId, s.ModelId));

        foreach (var model in allModels)
        {
            if (model.IsEmbedding) continue;
            if (string.IsNullOrWhiteSpace(model.ProviderId) || string.IsNullOrWhiteSpace(model.ModelId))
                continue;
            // 应用筛选
            if (!string.IsNullOrWhiteSpace(providerId)
                && !string.Equals(providerId, model.ProviderId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(modelId)
                && !string.Equals(modelId, model.ModelId, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = (model.ProviderId, model.ModelId);
            if (existingKeys.Contains(key)) continue;

            stats.Add(new TokenUsageStatsEntity
            {
                ProviderId = model.ProviderId,
                ModelId = model.ModelId,
                YearMonth = yearMonth,
                PromptTokens = 0,
                CompletionTokens = 0,
                CacheHitTokens = 0,
                CacheMissTokens = 0,
                RequestCount = 0,
                TotalCost = 0,
            });
        }

        // 按 Provider 分组。必须在补零值行之后执行，否则已配置但未使用的模型不会出现在 byProvider 输出中。
        var providerGroups = stats
            .GroupBy(s => s.ProviderId)
            .Select(pg =>
            {
                var models = pg.Select(s =>
                {
                    var cost = ResolveCostBreakdown(
                        s,
                        eventCostMap,
                        priceMap,
                        unambiguousPriceMap);

                    var totalCache = s.CacheHitTokens + s.CacheMissTokens;
                    var hitRate = totalCache > 0 ? (double)s.CacheHitTokens / totalCache : 0.0;

                    return new
                    {
                        modelId = s.ModelId,
                        promptTokens = s.PromptTokens,
                        completionTokens = s.CompletionTokens,
                        cacheHitTokens = s.CacheHitTokens,
                        cacheMissTokens = s.CacheMissTokens,
                        cacheHitRate = Math.Round(hitRate, 4),
                        inputCost = cost.InputCost,
                        cacheHitCost = cost.CacheHitCost,
                        outputCost = cost.OutputCost,
                        totalCost = cost.TotalCost,
                        requestCount = s.RequestCount,
                    };
                }).ToList();

                return new
                {
                    providerId = pg.Key,
                    promptTokens = pg.Sum(s => s.PromptTokens),
                    completionTokens = pg.Sum(s => s.CompletionTokens),
                    cacheHitTokens = pg.Sum(s => s.CacheHitTokens),
                    cacheMissTokens = pg.Sum(s => s.CacheMissTokens),
                    cacheHitRate = Math.Round(
                        (pg.Sum(s => s.CacheHitTokens) + pg.Sum(s => s.CacheMissTokens)) > 0
                            ? (double)pg.Sum(s => s.CacheHitTokens) / (pg.Sum(s => s.CacheHitTokens) + pg.Sum(s => s.CacheMissTokens))
                            : 0.0, 4),
                    inputCost = Math.Round(models.Sum(m => (decimal?)m.inputCost) ?? 0m, 6),
                    cacheHitCost = Math.Round(models.Sum(m => (decimal?)m.cacheHitCost) ?? 0m, 6),
                    outputCost = Math.Round(models.Sum(m => (decimal?)m.outputCost) ?? 0m, 6),
                    totalCost = Math.Round(models.Sum(m => (decimal?)m.totalCost) ?? 0m, 6),
                    requestCount = pg.Sum(s => s.RequestCount),
                    models,
                };
            }).ToList();

        var totalCacheHitTokens = stats.Sum(s => s.CacheHitTokens);
        var totalCacheMissTokens = stats.Sum(s => s.CacheMissTokens);
        var totalCacheTokens = totalCacheHitTokens + totalCacheMissTokens;
        var overallCacheHitRate = totalCacheTokens > 0 ? (double)totalCacheHitTokens / totalCacheTokens : 0.0;

        return Ok(new
        {
            yearMonth,
            totalPromptTokens = stats.Sum(s => s.PromptTokens),
            totalCompletionTokens = stats.Sum(s => s.CompletionTokens),
            totalCacheHitTokens,
            totalCacheMissTokens,
            cacheHitRate = Math.Round(overallCacheHitRate, 4),
            inputCost = Math.Round(providerGroups.Sum(p => (decimal?)p.inputCost) ?? 0m, 6),
            cacheHitCost = Math.Round(providerGroups.Sum(p => (decimal?)p.cacheHitCost) ?? 0m, 6),
            outputCost = Math.Round(providerGroups.Sum(p => (decimal?)p.outputCost) ?? 0m, 6),
            totalCost = Math.Round(providerGroups.Sum(p => (decimal?)p.totalCost) ?? 0m, 6),
            totalRequests = stats.Sum(s => s.RequestCount),
            byProvider = providerGroups,
        });
    }

    /// <summary>
    /// GET /api/stats/tokens/series?yearMonth=2026-06&amp;providerId=&amp;modelId=
    /// 返回指定月份的按日序列，以及同年度的按月序列，用于 Token 用量图表。
    /// </summary>
    [HttpGet("tokens/series")]
    public async Task<IActionResult> GetTokenStatsSeries(
        [FromQuery] string? yearMonth = null,
        [FromQuery] string? providerId = null,
        [FromQuery] string? modelId = null,
        CancellationToken ct = default)
    {
        yearMonth ??= DateTimeOffset.UtcNow.ToString("yyyy-MM");
        if (!DateTime.TryParse($"{yearMonth}-01", out var monthStart))
        {
            return BadRequest(new { message = "yearMonth must use yyyy-MM format." });
        }

        var year = monthStart.Year;
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

        var eventsQuery = ApplyTokenEventFilters(
            db.TokenUsageEvents.AsNoTracking(),
            providerId,
            modelId);

        var monthlyEvents = await eventsQuery
            .Where(e => e.YearMonth.StartsWith($"{year}-"))
            .Select(e => new
            {
                e.YearMonth,
                e.CacheMissTokens,
                e.CacheHitTokens,
                e.CompletionTokens,
                e.InputCost,
                e.CacheHitCost,
                e.OutputCost,
                e.TotalCost,
            })
            .ToListAsync(ct);

        var dailyEvents = await eventsQuery
            .Where(e => e.YearMonth == yearMonth)
            .Select(e => new
            {
                e.OccurredAtUtc,
                e.CacheMissTokens,
                e.CacheHitTokens,
                e.CompletionTokens,
                e.InputCost,
                e.CacheHitCost,
                e.OutputCost,
                e.TotalCost,
            })
            .ToListAsync(ct);

        var monthlyLookup = monthlyEvents
            .GroupBy(e => e.YearMonth)
            .ToDictionary(
                g => g.Key,
                g => new TokenSeriesPoint(
                    g.Key,
                    g.Sum(e => e.CacheMissTokens),
                    g.Sum(e => e.CacheHitTokens),
                    g.Sum(e => e.CompletionTokens),
                    g.LongCount(),
                    Math.Round(g.Sum(e => e.InputCost), 6),
                    Math.Round(g.Sum(e => e.CacheHitCost), 6),
                    Math.Round(g.Sum(e => e.OutputCost), 6),
                    Math.Round(g.Sum(e => e.TotalCost), 6)));

        var dailyLookup = dailyEvents
            .GroupBy(e => e.OccurredAtUtc.ToString("yyyy-MM-dd"))
            .ToDictionary(
                g => g.Key,
                g => new TokenSeriesPoint(
                    g.Key,
                    g.Sum(e => e.CacheMissTokens),
                    g.Sum(e => e.CacheHitTokens),
                    g.Sum(e => e.CompletionTokens),
                    g.LongCount(),
                    Math.Round(g.Sum(e => e.InputCost), 6),
                    Math.Round(g.Sum(e => e.CacheHitCost), 6),
                    Math.Round(g.Sum(e => e.OutputCost), 6),
                    Math.Round(g.Sum(e => e.TotalCost), 6)));

        var monthly = Enumerable.Range(1, 12)
            .Select(month =>
            {
                var period = $"{year}-{month:00}";
                return monthlyLookup.TryGetValue(period, out var point)
                    ? point
                    : TokenSeriesPoint.Empty(period);
            })
            .ToArray();

        var daily = Enumerable.Range(1, daysInMonth)
            .Select(day =>
            {
                var period = $"{yearMonth}-{day:00}";
                return dailyLookup.TryGetValue(period, out var point)
                    ? point
                    : TokenSeriesPoint.Empty(period);
            })
            .ToArray();

        return Ok(new
        {
            yearMonth,
            year,
            monthly,
            daily,
        });
    }

    /// <summary>
    /// GET /api/stats/tokens/events?from=&amp;to=&amp;providerId=&amp;modelId=&amp;sessionId=
    /// 查询 Token 使用事件明细（ADR-043）。
    /// 支持按时间范围、Provider、Model、Session 筛选。
    /// </summary>
    [HttpGet("tokens/events")]
    public async Task<IActionResult> GetTokenEvents(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? providerId = null,
        [FromQuery] string? modelId = null,
        [FromQuery] string? sessionId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = db.TokenUsageEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(from) && DateTimeOffset.TryParse(from, out var fromDate))
            query = query.Where(e => e.OccurredAtUtc >= fromDate);

        if (!string.IsNullOrWhiteSpace(to) && DateTimeOffset.TryParse(to, out var toDate))
            query = query.Where(e => e.OccurredAtUtc <= toDate);

        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(e => e.ProviderId == providerId);

        if (!string.IsNullOrWhiteSpace(modelId))
            query = query.Where(e => e.ModelId == modelId);

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);

        var total = await query.CountAsync(ct);

        var events = await query
            .OrderByDescending(e => e.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.SourceType,
                e.SourceId,
                e.WorkspaceId,
                e.SessionId,
                e.ProviderId,
                e.ModelId,
                e.OccurredAtUtc,
                e.YearMonth,
                e.PromptTokens,
                e.CompletionTokens,
                e.TotalTokens,
                e.CacheHitTokens,
                e.CacheMissTokens,
                e.CacheEligibleTokens,
                e.CacheHitRate,
                e.InputCost,
                e.OutputCost,
                e.CacheHitCost,
                e.TotalCost,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            total,
            page,
            pageSize,
            events,
        });
    }

    /// <summary>
    /// GET /api/stats/tokens/context-layers?from=&amp;to=&amp;providerId=&amp;modelId=&amp;sessionId=
    /// 返回上下文层级 token 占比、缓存命中率中位数和易变性统计。
    /// </summary>
    [HttpGet("tokens/context-layers")]
    public async Task<IActionResult> GetContextLayerTokenStats(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? providerId = null,
        [FromQuery] string? modelId = null,
        [FromQuery] string? sessionId = null,
        CancellationToken ct = default)
    {
        var query = db.ContextLayerMetricEvents.AsNoTracking();

        DateTimeOffset? fromDate = null;
        if (!string.IsNullOrWhiteSpace(from) && DateTimeOffset.TryParse(from, out var parsedFrom))
            fromDate = parsedFrom;

        DateTimeOffset? toDate = null;
        if (!string.IsNullOrWhiteSpace(to) && DateTimeOffset.TryParse(to, out var parsedTo))
            toDate = parsedTo;

        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(e => e.ProviderId == providerId);

        if (!string.IsNullOrWhiteSpace(modelId))
            query = query.Where(e => e.ModelId == modelId);

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);

        var rows = (await query
            .OrderBy(e => e.LayerOrder)
            .ThenBy(e => e.LayerName)
            .ToListAsync(ct))
            .Where(e => !fromDate.HasValue || e.OccurredAtUtc >= fromDate.Value)
            .Where(e => !toDate.HasValue || e.OccurredAtUtc <= toDate.Value)
            .ToList();
        var totalTokens = rows.Sum(e => e.TokenCount);
        var layers = rows
            .GroupBy(e => new { e.LayerName, e.LayerOrder, e.LayerRole })
            .OrderBy(g => g.Key.LayerOrder)
            .ThenBy(g => g.Key.LayerName)
            .Select(g =>
            {
                var items = g.ToList();
                var cacheRates = items
                    .Select(e => e.EstimatedCacheHitRate)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();
                var tokenCount = items.Sum(e => e.TokenCount);
                var changeReasons = items
                    .Where(e => !string.IsNullOrWhiteSpace(e.ChangeReason))
                    .GroupBy(e => e.ChangeReason!)
                    .OrderByDescending(rg => rg.Count())
                    .Select(rg => new
                    {
                        reason = rg.Key,
                        count = rg.Count(),
                    })
                    .ToArray();

                return new
                {
                    layerName = g.Key.LayerName,
                    layerOrder = g.Key.LayerOrder,
                    layerRole = g.Key.LayerRole,
                    calls = items.Count,
                    tokenCount,
                    tokenShare = RoundRatio(totalTokens > 0 ? (double)tokenCount / totalTokens : 0),
                    avgTokens = Math.Round(items.Average(e => e.TokenCount), 2),
                    medianTokens = Median(items.Select(e => (double)e.TokenCount)),
                    p95Tokens = Percentile(items.Select(e => (double)e.TokenCount), 95),
                    estimatedHitTokens = items.Sum(e => e.EstimatedCacheHitTokens),
                    estimatedMissTokens = items.Sum(e => e.EstimatedCacheMissTokens),
                    avgCacheHitRate = cacheRates.Count == 0 ? 0 : RoundRatio(cacheRates.Average()),
                    medianCacheHitRate = Median(cacheRates),
                    changeCount = items.Count(e => e.IsChanged),
                    changeRate = RoundRatio(items.Count == 0 ? 0 : (double)items.Count(e => e.IsChanged) / items.Count),
                    distinctHashes = items.Select(e => e.ContentHash).Distinct(StringComparer.Ordinal).Count(),
                    changeReasons,
                };
            })
            .ToArray();

        return Ok(new
        {
            from,
            to,
            providerId,
            modelId,
            sessionId,
            totalEvents = rows.Count,
            totalLayerTokens = totalTokens,
            layers,
        });
    }

    /// <summary>
    /// POST /api/stats/tokens/rebuild
    /// 从 ChatMessages.UsageJson 重建 TokenUsageEventEntity 明细账本。
    /// 仅管理员可用。
    /// 可选参数 yearMonth=2026-05 限制重建范围。
    /// </summary>
    [HttpPost("tokens/rebuild")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> RebuildTokenEvents(
        [FromQuery] string? yearMonth = null,
        CancellationToken ct = default)
    {
        var result = await rebuildService.RebuildAsync(yearMonth, ct);
        return Ok(result);
    }

    private static IQueryable<TokenUsageEventEntity> ApplyTokenEventFilters(
        IQueryable<TokenUsageEventEntity> query,
        string? providerId,
        string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            query = providerId == "unknown"
                ? query.Where(e => e.ProviderId == null || e.ProviderId == "unknown")
                : query.Where(e => e.ProviderId == providerId);
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            query = modelId == "unknown"
                ? query.Where(e => e.ModelId == null || e.ModelId == "unknown")
                : query.Where(e => e.ModelId == modelId);
        }

        return query;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0;
        var mid = sorted.Length / 2;
        var value = sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
        return Math.Round(value, 6);
    }

    private static double Percentile(IEnumerable<double> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0;
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return Math.Round(sorted[lower], 6);
        var weight = rank - lower;
        return Math.Round(sorted[lower] * (1 - weight) + sorted[upper] * weight, 6);
    }

    private static double RoundRatio(double value) => Math.Round(value, 6);

    private static TokenCostBreakdown ResolveCostBreakdown(
        TokenUsageStatsEntity stats,
        IReadOnlyDictionary<(string ProviderId, string ModelId), TokenCostBreakdown> eventCostMap,
        IReadOnlyDictionary<(string ProviderId, string ModelId), TokenPrice> priceMap,
        IReadOnlyDictionary<string, TokenPrice> unambiguousPriceMap)
    {
        if (eventCostMap.TryGetValue((stats.ProviderId, stats.ModelId), out var recorded))
        {
            return recorded;
        }

        if (!priceMap.TryGetValue((stats.ProviderId, stats.ModelId), out var price))
        {
            unambiguousPriceMap.TryGetValue(stats.ModelId, out price);
        }

        var inputPrice = price?.InputPricePer1MTokens ?? 0m;
        var outputPrice = price?.OutputPricePer1MTokens ?? 0m;
        var cacheHitPrice = price?.CacheHitPricePer1MTokens ?? inputPrice;

        return new TokenCostBreakdown(
            Math.Round(stats.CacheMissTokens / 1_000_000m * inputPrice, 6),
            Math.Round(stats.CacheHitTokens / 1_000_000m * cacheHitPrice, 6),
            Math.Round(stats.CompletionTokens / 1_000_000m * outputPrice, 6));
    }

    private sealed record TokenSeriesPoint(
        string Period,
        long CacheMissTokens,
        long CacheHitTokens,
        long CompletionTokens,
        long RequestCount,
        decimal InputCost,
        decimal CacheHitCost,
        decimal OutputCost,
        decimal TotalCost)
    {
        public static TokenSeriesPoint Empty(string period) => new(period, 0, 0, 0, 0, 0m, 0m, 0m, 0m);
    }

    private sealed record TokenPrice(
        decimal InputPricePer1MTokens,
        decimal OutputPricePer1MTokens,
        decimal CacheHitPricePer1MTokens);

    private sealed record TokenCostRow(
        string ProviderId,
        string ModelId,
        decimal InputCost,
        decimal CacheHitCost,
        decimal OutputCost);

    private sealed record TokenCostBreakdown(
        decimal InputCost,
        decimal CacheHitCost,
        decimal OutputCost)
    {
        public decimal TotalCost => Math.Round(InputCost + CacheHitCost + OutputCost, 6);
    }
}
