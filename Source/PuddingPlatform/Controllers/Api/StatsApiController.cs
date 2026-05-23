using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    TokenUsageRebuildService rebuildService) : ControllerBase
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
                totalCost = 0m,
                totalRequests = 0L,
                byProvider = Array.Empty<object>(),
            });
        }

        // 加载价格配置（用于费用计算）
        var priceMap = await db.LlmModels.AsNoTracking()
            .Select(m => new
            {
                m.ModelId,
                m.InputPricePer1MTokens,
                m.OutputPricePer1MTokens,
                m.CacheHitPricePer1MTokens,
            })
            .ToDictionaryAsync(m => m.ModelId, ct);

        // 按 Provider 分组
        var providerGroups = stats
            .GroupBy(s => s.ProviderId)
            .Select(pg =>
            {
                var models = pg.Select(s =>
                {
                    priceMap.TryGetValue(s.ModelId, out var price);
                    var inputPrice = price?.InputPricePer1MTokens ?? 0m;
                    var outputPrice = price?.OutputPricePer1MTokens ?? 0m;
                    var cacheHitPrice = price?.CacheHitPricePer1MTokens ?? inputPrice;

                    // 费用：cacheHit × cacheHitPrice + cacheMiss × inputPrice + completion × outputPrice
                    var cost = (s.CacheHitTokens / 1_000_000m * cacheHitPrice)
                             + (s.CacheMissTokens / 1_000_000m * inputPrice)
                             + (s.CompletionTokens / 1_000_000m * outputPrice);

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
                        totalCost = Math.Round(cost, 6),
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
            totalCost = providerGroups.Sum(p => (decimal?)p.totalCost) ?? 0m,
            totalRequests = stats.Sum(s => s.RequestCount),
            byProvider = providerGroups,
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
}
