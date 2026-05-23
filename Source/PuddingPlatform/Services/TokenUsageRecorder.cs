using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Token 使用事件记录器（ADR-043）。
/// 从 chat done 帧或消息持久化结果中记录一条 TokenUsageEventEntity，并更新月度聚合。
/// 失败仅记录 warning，不影响调用方主流程。
/// </summary>
public class TokenUsageRecorder(
    IServiceScopeFactory scopeFactory,
    TokenUsageNormalizer normalizer,
    ILogger<TokenUsageRecorder> logger)
{
    /// <summary>
    /// 记录一条 token usage 事件。
    /// 幂等：同一 (SourceType, SourceId) 重复调用不会重复计数。
    /// </summary>
    public async Task RecordAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        DateTimeOffset? occurredAtUtc = null)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // 幂等检查
            var exists = await db.Set<TokenUsageEventEntity>()
                .AnyAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);

            if (exists)
            {
                logger.LogDebug(
                    "[TokenUsageRecorder] Skip duplicate source={SourceType}/{SourceId}",
                    sourceType, sourceId);
                return;
            }

            occurredAtUtc ??= DateTimeOffset.UtcNow;
            var yearMonth = occurredAtUtc.Value.ToString("yyyy-MM");

            // 查询价格配置
            var inputPrice = 0m;
            var outputPrice = 0m;
            var cacheHitPrice = 0m;

            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var priceConfig = await db.LlmModels.AsNoTracking()
                    .Where(m => m.ModelId == modelId)
                    .Select(m => new { m.InputPricePer1MTokens, m.OutputPricePer1MTokens, m.CacheHitPricePer1MTokens })
                    .FirstOrDefaultAsync();

                if (priceConfig is not null)
                {
                    inputPrice = priceConfig.InputPricePer1MTokens;
                    outputPrice = priceConfig.OutputPricePer1MTokens;
                    cacheHitPrice = priceConfig.CacheHitPricePer1MTokens > 0
                        ? priceConfig.CacheHitPricePer1MTokens
                        : inputPrice;
                }
            }

            // 归一化计算
            var normalized = normalizer.Normalize(usage, inputPrice, outputPrice, cacheHitPrice);

            // 写入明细账本
            var rawJson = System.Text.Json.JsonSerializer.Serialize(usage, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

            db.Set<TokenUsageEventEntity>().Add(new TokenUsageEventEntity
            {
                SourceType = sourceType,
                SourceId = sourceId,
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                ProviderId = providerId,
                ModelId = modelId,
                OccurredAtUtc = occurredAtUtc.Value,
                YearMonth = yearMonth,
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

            // 更新月度聚合
            var providerIdVal = providerId ?? "unknown";
            var modelIdVal = modelId ?? "unknown";

            var stats = await db.TokenUsageStats
                .FirstOrDefaultAsync(s => s.YearMonth == yearMonth
                    && s.ProviderId == providerIdVal
                    && s.ModelId == modelIdVal);

            if (stats is not null)
            {
                stats.PromptTokens += normalized.PromptTokens;
                stats.CompletionTokens += normalized.CompletionTokens;
                stats.CacheHitTokens += normalized.CacheHitTokens;
                stats.CacheMissTokens += normalized.CacheMissTokens;
                stats.RequestCount++;
                stats.TotalCost += normalized.TotalCost;
                stats.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.TokenUsageStats.Add(new TokenUsageStatsEntity
                {
                    ProviderId = providerIdVal,
                    ModelId = modelIdVal,
                    YearMonth = yearMonth,
                    PromptTokens = normalized.PromptTokens,
                    CompletionTokens = normalized.CompletionTokens,
                    CacheHitTokens = normalized.CacheHitTokens,
                    CacheMissTokens = normalized.CacheMissTokens,
                    RequestCount = 1,
                    TotalCost = normalized.TotalCost,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();

            logger.LogDebug(
                "[TokenUsageRecorder] Recorded source={SourceType}/{SourceId} provider={Provider} model={Model} cost={Cost}",
                sourceType, sourceId, providerIdVal, modelIdVal, normalized.TotalCost);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[TokenUsageRecorder] Failed to record token usage source={SourceType}/{SourceId}",
                sourceType, sourceId);
        }
    }
}
