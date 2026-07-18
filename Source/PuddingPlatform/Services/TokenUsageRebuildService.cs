using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 从 Conversation Event Store 的 usage.recorded v2 事实事件重建 Token 明细账本和月度汇总。
/// Provider/Model 必须来自执行时不可变 LLM Profile；禁止从当前 Agent 配置或默认 Provider 猜测。
/// </summary>
public sealed class TokenUsageRebuildService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TokenUsageNormalizer normalizer,
    ILlmConfigService? llmConfigService,
    ILogger<TokenUsageRebuildService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed class RebuildResult
    {
        public int EventsCreated { get; set; }
        public int EventsDeleted { get; set; }
        public int UsageEventsScanned { get; set; }
        public int UnattributedEventsSkipped { get; set; }
        public int StatsRowsRebuilt { get; set; }
        public int Errors { get; set; }
        public List<string> ErrorDetails { get; set; } = [];
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public async Task<RebuildResult> RebuildAsync(
        string? yearMonth = null,
        CancellationToken ct = default)
    {
        var result = new RebuildResult();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var persistedEvents = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.Type == ConversationEventTypes.UsageRecorded)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        var usageEvents = persistedEvents
            .Select(TryMapUsageEvent)
            .Where(mapped => mapped is not null)
            .Select(mapped => mapped!)
            .Where(mapped => string.IsNullOrWhiteSpace(yearMonth)
                || mapped.OccurredAtUtc.ToString("yyyy-MM") == yearMonth)
            .ToList();

        result.UsageEventsScanned = usageEvents.Count;

        var reconstructableEvents = usageEvents
            .Where(persisted =>
                persisted.Usage is not null
                && !string.IsNullOrWhiteSpace(persisted.ProviderId)
                && !string.IsNullOrWhiteSpace(persisted.ModelId))
            .Select(persisted => new ReconstructableUsageEvent(
                persisted.EventId,
                persisted.ConversationId,
                persisted.WorkspaceId,
                persisted.OccurredAtUtc,
                persisted.ProviderId!,
                persisted.ModelId!,
                persisted.Usage!))
            .ToList();
        result.UnattributedEventsSkipped = usageEvents.Count - reconstructableEvents.Count;

        var prices = BuildPriceMap(llmConfigService?.GetAllModels() ?? []);
        var newEvents = new List<TokenUsageEventEntity>();

        foreach (var persisted in reconstructableEvents)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var price = prices.TryGetValue(
                    PriceKey(persisted.ProviderId, persisted.ModelId),
                    out var configured)
                    ? configured
                    : TokenPrice.Zero;
                var cacheHitPrice = price.CacheHitPricePer1MTokens > 0
                    ? price.CacheHitPricePer1MTokens
                    : price.InputPricePer1MTokens;
                var normalized = normalizer.Normalize(
                    persisted.Usage,
                    price.InputPricePer1MTokens,
                    price.OutputPricePer1MTokens,
                    cacheHitPrice);

                newEvents.Add(new TokenUsageEventEntity
                {
                    SourceType = "agent_llm",
                    SourceId = persisted.EventId,
                    WorkspaceId = persisted.WorkspaceId,
                    SessionId = persisted.ConversationId,
                    ProviderId = persisted.ProviderId,
                    ModelId = persisted.ModelId,
                    OccurredAtUtc = persisted.OccurredAtUtc,
                    YearMonth = persisted.OccurredAtUtc.ToString("yyyy-MM"),
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
                    RawUsageJson = JsonSerializer.Serialize(persisted.Usage, JsonOpts),
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorDetails.Add($"event#{persisted.EventId}: {ex.Message}");
                logger.LogWarning(
                    ex,
                    "[TokenUsageRebuild] Failed usage event={EventId}",
                    persisted.EventId);
            }
        }

        // Rebuild must be lossless. Only replace ledger rows for facts that were
        // successfully reconstructed. Deletion and insertion share the same
        // transaction/save boundary, so a write failure rolls the replacement back.
        var rebuiltSourceIds = newEvents
            .Select(rebuilt => rebuilt.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var derivedEventsQuery = db.TokenUsageEvents
            .Where(e => e.SourceType == "agent_llm" || e.SourceType == "chat_message");
        if (!string.IsNullOrWhiteSpace(yearMonth))
        {
            derivedEventsQuery = derivedEventsQuery.Where(e => e.YearMonth == yearMonth);
        }

        var derivedEvents = (await derivedEventsQuery.ToListAsync(ct))
            .Where(existing => rebuiltSourceIds.Contains(existing.SourceId))
            .ToList();
        if (derivedEvents.Count > 0)
        {
            db.TokenUsageEvents.RemoveRange(derivedEvents);
            result.EventsDeleted = derivedEvents.Count;
        }

        if (newEvents.Count > 0)
        {
            db.TokenUsageEvents.AddRange(newEvents);
            result.EventsCreated = newEvents.Count;
        }

        if (derivedEvents.Count > 0 || newEvents.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        result.StatsRowsRebuilt = await RebuildMonthlyStatsAsync(db, yearMonth, ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "[TokenUsageRebuild] Complete scanned={Scanned} deleted={Deleted} created={Created} unattributed={Unattributed} statsRows={StatsRows} errors={Errors}",
            result.UsageEventsScanned,
            result.EventsDeleted,
            result.EventsCreated,
            result.UnattributedEventsSkipped,
            result.StatsRowsRebuilt,
            result.Errors);

        return result;
    }

    private static PersistedUsageEvent? TryMapUsageEvent(ConversationEventEntity entity)
    {
        if (!DateTimeOffset.TryParse(entity.OccurredAt, out var occurredAt))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(entity.Payload);
            var payload = document.RootElement;
            TokenUsageDto? usage = null;
            string? providerId = null;
            string? modelId = null;

            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = JsonSerializer.Deserialize<TokenUsageDto>(
                    usageElement.GetRawText(),
                    JsonOpts);
                providerId = ReadString(payload, "providerId");
                modelId = ReadString(payload, "modelId");
            }

            return new PersistedUsageEvent(
                entity.EventId,
                entity.ConversationId,
                entity.WorkspaceId,
                occurredAt,
                providerId,
                modelId,
                usage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, TokenPrice> BuildPriceMap(
        IReadOnlyList<LlmModelInfo> models)
    {
        return models
            .Where(model =>
                !string.IsNullOrWhiteSpace(model.ProviderId)
                && !string.IsNullOrWhiteSpace(model.ModelId))
            .GroupBy(model => PriceKey(model.ProviderId, model.ModelId))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var model = group.First();
                    return new TokenPrice(
                        model.InputPricePer1MTokens,
                        model.OutputPricePer1MTokens,
                        model.CacheHitPricePer1MTokens);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static string PriceKey(string providerId, string modelId)
        => $"{providerId}\u001f{modelId}";

    private static string? ReadString(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

        var eventsQuery = db.TokenUsageEvents.AsNoTracking();
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
            .Select(group => new TokenUsageStatsEntity
            {
                YearMonth = group.Key.YearMonth,
                ProviderId = group.Key.ProviderId,
                ModelId = group.Key.ModelId,
                PromptTokens = group.Sum(e => e.PromptTokens),
                CompletionTokens = group.Sum(e => e.CompletionTokens),
                CacheHitTokens = group.Sum(e => e.CacheHitTokens),
                CacheMissTokens = group.Sum(e => e.CacheMissTokens),
                RequestCount = group.LongCount(),
                TotalCost = group.Sum(e => e.TotalCost),
                UpdatedAt = now,
            })
            .ToListAsync(ct);

        if (rebuiltStats.Count > 0)
        {
            db.TokenUsageStats.AddRange(rebuiltStats);
            await db.SaveChangesAsync(ct);
        }

        return rebuiltStats.Count;
    }

    private sealed record PersistedUsageEvent(
        string EventId,
        string ConversationId,
        string WorkspaceId,
        DateTimeOffset OccurredAtUtc,
        string? ProviderId,
        string? ModelId,
        TokenUsageDto? Usage);

    private sealed record ReconstructableUsageEvent(
        string EventId,
        string ConversationId,
        string WorkspaceId,
        DateTimeOffset OccurredAtUtc,
        string ProviderId,
        string ModelId,
        TokenUsageDto Usage);

    private sealed record TokenPrice(
        decimal InputPricePer1MTokens,
        decimal OutputPricePer1MTokens,
        decimal CacheHitPricePer1MTokens)
    {
        public static TokenPrice Zero { get; } = new(0m, 0m, 0m);
    }
}
