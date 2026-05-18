using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Token 使用统计 API — 按月/按 Provider/按 Model 聚合查询。
/// ADR-018：上下文缓存可观测性体系。
/// </summary>
[Authorize]
[ApiController]
[Route("api/stats")]
public class StatsApiController(PlatformDbContext db) : ControllerBase
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
}
