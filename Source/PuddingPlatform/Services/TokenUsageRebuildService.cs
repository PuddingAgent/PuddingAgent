using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    ILogger<TokenUsageRebuildService> logger)
{
    public sealed class RebuildResult
    {
        public int EventsCreated { get; set; }
        public int MessagesScanned { get; set; }
        public int SkippedDuplicates { get; set; }
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

        // 查询所有有 UsageJson 的消息
        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.UsageJson != null && m.UsageJson != "");

        var messages = await query
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.SessionId, m.UsageJson, m.CreatedAt })
            .ToListAsync(ct);

        // 如果指定了月份，在后端过滤
        if (!string.IsNullOrWhiteSpace(yearMonth))
        {
            messages = messages
                .Where(m => DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt).ToString("yyyy-MM") == yearMonth)
                .ToList();
        }

        result.MessagesScanned = messages.Count;

        // 获取已存在的 source id 集合（避免重复）
        var existingSourceIds = await db.Set<TokenUsageEventEntity>()
            .Where(e => e.SourceType == "chat_message")
            .Select(e => e.SourceId)
            .ToHashSetAsync(ct);

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
                var usage = System.Text.Json.JsonSerializer.Deserialize<TokenUsageDto>(
                    msg.UsageJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    { PropertyNameCaseInsensitive = true });

                if (usage is null) continue;

                var occurredAt = msg.CreatedAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.CreatedAt)
                    : DateTimeOffset.UtcNow;
                var ym = occurredAt.ToString("yyyy-MM");

                var normalized = normalizer.Normalize(usage);
                var rawJson = System.Text.Json.JsonSerializer.Serialize(usage,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

                newEvents.Add(new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = sourceId,
                    SessionId = msg.SessionId?.ToString(),
                    OccurredAtUtc = occurredAt,
                    YearMonth = ym,
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

        logger.LogInformation(
            "[Rebuild] Complete: scanned={Scanned} created={Created} skipped={Skipped} errors={Errors}",
            result.MessagesScanned, result.EventsCreated, result.SkippedDuplicates, result.Errors);

        return result;
    }
}
