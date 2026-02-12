using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Token 使用统计重建服务（ADR-043）。
/// 从 ChatMessages.UsageJson 回扫，重建 TokenUsageEventEntity 和 TokenUsageStatsEntity。
/// 用于修复历史数据、验证聚合一致性。
/// </summary>
public class TokenUsageRebuildService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TokenUsageNormalizer normalizer,
    ILlmConfigService? llmConfigService,
    ILogger<TokenUsageRebuildService> logger)
{
    public sealed class RebuildResult
    {
        public int EventsCreated { get; set; }
        public int EventsDeleted { get; set; }
        public int MessagesScanned { get; set; }
        public int SkippedDuplicates { get; set; }
        public int StatsRowsRebuilt { get; set; }
        public int Errors { get; set; }
        public List<string> ErrorDetails { get; set; } = [];
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 执行重建。
    /// 如果 yearMonth 不为空，则只重建指定月份；否则重建全部历史数据。
    /// </summary>
    public async Task<RebuildResult> RebuildAsync(string? yearMonth = null, CancellationToken ct = default)
    {
        var result = new RebuildResult();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 查询所有有 UsageJson 的消息
        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.UsageJson != null && m.UsageJson != "");

        var messages = await query
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.SessionId, m.UsageJson, m.CreatedAt, m.AgentTemplateId })
            .ToListAsync(ct);

        // 如果指定了月份，在后端过滤
        if (!string.IsNullOrWhiteSpace(yearMonth))
        {
            messages = messages
                .Where(m => DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt).ToString("yyyy-MM") == yearMonth)
                .ToList();
        }

        result.MessagesScanned = messages.Count;

        var rebuildSourceIds = messages.Select(m => m.Id.ToString()).ToHashSet();
        var preservedModelRoutes = new Dictionary<string, ModelRoute>(StringComparer.Ordinal);
        if (rebuildSourceIds.Count > 0)
        {
            var existingGeneratedEventsQuery = db.Set<TokenUsageEventEntity>()
                .Where(e => e.SourceType == "chat_message" && rebuildSourceIds.Contains(e.SourceId));

            if (!string.IsNullOrWhiteSpace(yearMonth))
            {
                existingGeneratedEventsQuery = existingGeneratedEventsQuery.Where(e => e.YearMonth == yearMonth);
            }

            var existingGeneratedEvents = await existingGeneratedEventsQuery.ToListAsync(ct);
            foreach (var ev in existingGeneratedEvents)
            {
                if (!string.IsNullOrWhiteSpace(ev.ProviderId) && !string.IsNullOrWhiteSpace(ev.ModelId))
                {
                    preservedModelRoutes[ev.SourceId] = new ModelRoute(ev.ProviderId!, ev.ModelId!);
                }
            }

            if (existingGeneratedEvents.Count > 0)
            {
                db.Set<TokenUsageEventEntity>().RemoveRange(existingGeneratedEvents);
                await db.SaveChangesAsync(ct);
                result.EventsDeleted = existingGeneratedEvents.Count;
            }
        }

        // 获取已存在的 source id 集合（避免重复）
        var existingSourceIds = await db.Set<TokenUsageEventEntity>()
            .Where(e => e.SourceType == "chat_message")
            .Select(e => e.SourceId)
            .ToHashSetAsync(ct);
        var existingRecordedUsages = await db.Set<TokenUsageEventEntity>()
            .Where(e => e.SourceType == "chat_message" && e.RawUsageJson != null)
            .Select(e => new RecordedUsage(e.SessionId, e.RawUsageJson!, e.OccurredAtUtc))
            .ToListAsync(ct);

        // 从 llm.providers.json 构建 model → (providerId, modelId) 查找表
        // 用于回填重建事件中缺失的 ProviderId/ModelId
        // 唯一配置来源：数据根目录下的 config/llm.providers.json，实际根路径由 PuddingDataPaths 决定。
        var modelToProvider = new Dictionary<string, (string ProviderId, string ModelId)>(StringComparer.OrdinalIgnoreCase);
        (string ProviderId, string ModelId)? defaultProvider = null;
        var priceMap = new Dictionary<(string ProviderId, string ModelId), TokenPrice>();
        var unambiguousPriceMap = new Dictionary<string, TokenPrice>(StringComparer.OrdinalIgnoreCase);

        var allModels = llmConfigService?.GetAllModels() ?? [];
        foreach (var m in allModels)
        {
            if (!string.IsNullOrWhiteSpace(m.ModelId))
            {
                modelToProvider.TryAdd(m.ModelId, (m.ProviderId, m.ModelId));
                defaultProvider ??= (m.ProviderId, m.ModelId);
                priceMap[(m.ProviderId, m.ModelId)] = new TokenPrice(
                    m.InputPricePer1MTokens,
                    m.OutputPricePer1MTokens,
                    m.CacheHitPricePer1MTokens);
            }
        }

        foreach (var group in allModels
            .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
            .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1))
        {
            var model = group.First();
            unambiguousPriceMap[group.Key] = new TokenPrice(
                model.InputPricePer1MTokens,
                model.OutputPricePer1MTokens,
                model.CacheHitPricePer1MTokens);
        }

        var newEvents = new List<TokenUsageEventEntity>();

        foreach (var msg in messages)
        {
            if (ct.IsCancellationRequested) break;

            var sourceId = msg.Id.ToString();

            // 跳过已存在的
            if (existingSourceIds.Contains(sourceId))
            {
                result.SkippedDuplicates++;
                continue;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(msg.UsageJson))
                {
                    continue;
                }

                var usage = System.Text.Json.JsonSerializer.Deserialize<TokenUsageDto>(
                    msg.UsageJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    { PropertyNameCaseInsensitive = true });

                if (usage is null) continue;
                if (!HasTokenValues(usage)) continue;

                var occurredAt = msg.CreatedAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.CreatedAt)
                    : DateTimeOffset.UtcNow;
                var ym = occurredAt.ToString("yyyy-MM");

                var rawJson = System.Text.Json.JsonSerializer.Serialize(usage,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

                if (HasRecordedUsage(existingRecordedUsages, msg.SessionId, rawJson, occurredAt))
                {
                    result.SkippedDuplicates++;
                    continue;
                }

                var providerId = (string?)null;
                var modelId = (string?)null;
                if (preservedModelRoutes.TryGetValue(sourceId, out var preservedRoute))
                {
                    providerId = preservedRoute.ProviderId;
                    modelId = preservedRoute.ModelId;
                }
                else if (defaultProvider.HasValue && modelToProvider.Count > 0)
                {
                    providerId = defaultProvider.Value.ProviderId;
                    modelId = defaultProvider.Value.ModelId;
                }

                var price = ResolvePrice(providerId, modelId, priceMap, unambiguousPriceMap);
                var cacheHitPrice = price.CacheHitPricePer1MTokens > 0
                    ? price.CacheHitPricePer1MTokens
                    : price.InputPricePer1MTokens;
                var normalized = normalizer.Normalize(
                    usage,
                    price.InputPricePer1MTokens,
                    price.OutputPricePer1MTokens,
                    cacheHitPrice);

                newEvents.Add(new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = sourceId,
                    SessionId = msg.SessionId?.ToString(),
                    OccurredAtUtc = occurredAt,
                    YearMonth = ym,
                    ProviderId = providerId,
                    ModelId = modelId,
                    PromptTokens = normalized.PromptTokens,
                    CompletionTokens = normalized.CompletionTokens,
                    TotalTokens = normalized.TotalTokens,
                    CacheHitTokens = normalized.CacheHitTokens,
                    CacheMissTokens = normalized.CacheMissTokens,
                    CacheEligibleTokens = normalized.CacheEligibleTokens,
                    CacheHitRate = normalized.CacheHitRate,
                    InputCost = normalized.InputCost,
                    OutputCost = normalized.OutputCost,
                    CacheHitCost = normalized.CacheHitCost,
                    TotalCost = normalized.TotalCost,
                    RawUsageJson = rawJson,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                existingSourceIds.Add(sourceId);
                existingRecordedUsages.Add(new RecordedUsage(msg.SessionId, rawJson, occurredAt));
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorDetails.Add($"msg#{sourceId}: {ex.Message}");
                logger.LogWarning(ex, "[Rebuild] Error processing message {MessageId}", sourceId);
            }
        }

        // 批量写入明细账本
        if (newEvents.Count > 0)
        {
            db.Set<TokenUsageEventEntity>().AddRange(newEvents);
            await db.SaveChangesAsync(ct);
            result.EventsCreated = newEvents.Count;
        }

        result.StatsRowsRebuilt = await RebuildMonthlyStatsAsync(db, yearMonth, ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "[Rebuild] Complete: scanned={Scanned} deleted={Deleted} created={Created} skipped={Skipped} statsRows={StatsRows} errors={Errors}",
            result.MessagesScanned, result.EventsDeleted, result.EventsCreated, result.SkippedDuplicates, result.StatsRowsRebuilt, result.Errors);

        return result;
    }

    private sealed record RecordedUsage(string? SessionId, string RawUsageJson, DateTimeOffset OccurredAtUtc);

    private sealed record ModelRoute(string ProviderId, string ModelId);

    private sealed record TokenPrice(
        decimal InputPricePer1MTokens,
        decimal OutputPricePer1MTokens,
        decimal CacheHitPricePer1MTokens);

    private static TokenPrice ResolvePrice(
        string? providerId,
        string? modelId,
        IReadOnlyDictionary<(string ProviderId, string ModelId), TokenPrice> priceMap,
        IReadOnlyDictionary<string, TokenPrice> unambiguousPriceMap)
    {
        if (!string.IsNullOrWhiteSpace(providerId)
            && !string.IsNullOrWhiteSpace(modelId)
            && priceMap.TryGetValue((providerId, modelId), out var scopedPrice))
        {
            return scopedPrice;
        }

        if (!string.IsNullOrWhiteSpace(modelId)
            && unambiguousPriceMap.TryGetValue(modelId, out var unambiguousPrice))
        {
            return unambiguousPrice;
        }

        return new TokenPrice(0m, 0m, 0m);
    }

    private static bool HasTokenValues(TokenUsageDto usage)
    {
        return (usage.PromptTokens ?? 0) > 0
            || (usage.CompletionTokens ?? 0) > 0
            || (usage.TotalTokens ?? 0) > 0
            || (usage.PromptCacheHitTokens ?? 0) > 0
            || (usage.PromptCacheMissTokens ?? 0) > 0;
    }

    private static bool HasRecordedUsage(
        IReadOnlyList<RecordedUsage> existingRecordedUsages,
        string? sessionId,
        string rawUsageJson,
        DateTimeOffset occurredAt)
    {
        return existingRecordedUsages.Any(existing =>
            string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(existing.RawUsageJson, rawUsageJson, StringComparison.Ordinal)
            && Math.Abs((existing.OccurredAtUtc - occurredAt).TotalSeconds) <= 5);
    }

    private static async Task<int> RebuildMonthlyStatsAsync(
        PlatformDbContext db,
        string? yearMonth,
        CancellationToken ct)
    {
        var existingStatsQuery = db.TokenUsageStats.AsQueryable();
        if (!string.IsNullOrWhiteSpace(yearMonth))
        {
            existingStatsQuery = existingStatsQuery.Where(s => s.YearMonth == yearMonth);
        }

        var existingStats = await existingStatsQuery.ToListAsync(ct);
        if (existingStats.Count > 0)
        {
            db.TokenUsageStats.RemoveRange(existingStats);
            await db.SaveChangesAsync(ct);
        }

        var eventsQuery = db.Set<TokenUsageEventEntity>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(yearMonth))
        {
            eventsQuery = eventsQuery.Where(e => e.YearMonth == yearMonth);
        }

        var now = DateTimeOffset.UtcNow;
        var rebuiltStats = await eventsQuery
            .GroupBy(e => new
            {
                e.YearMonth,
                ProviderId = e.ProviderId ?? "unknown",
                ModelId = e.ModelId ?? "unknown",
            })
            .Select(g => new TokenUsageStatsEntity
            {
                YearMonth = g.Key.YearMonth,
                ProviderId = g.Key.ProviderId,
                ModelId = g.Key.ModelId,
                PromptTokens = g.Sum(e => e.PromptTokens),
                CompletionTokens = g.Sum(e => e.CompletionTokens),
                CacheHitTokens = g.Sum(e => e.CacheHitTokens),
                CacheMissTokens = g.Sum(e => e.CacheMissTokens),
                RequestCount = g.LongCount(),
                TotalCost = g.Sum(e => e.TotalCost),
                UpdatedAt = now,
            })
            .ToListAsync(ct);

        if (rebuiltStats.Count == 0)
        {
            return 0;
        }

        db.TokenUsageStats.AddRange(rebuiltStats);
        await db.SaveChangesAsync(ct);
        return rebuiltStats.Count;
    }
}
