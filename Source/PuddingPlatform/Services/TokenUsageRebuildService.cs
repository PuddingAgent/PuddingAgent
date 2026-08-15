using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
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
        public int GatewayEventsCreated { get; set; }
        public int GatewayEventsDeleted { get; set; }
        public int GatewayActivitiesScanned { get; set; }
        public int GatewayFactsSkipped { get; set; }
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

        await RebuildGatewayUsageAsync(db, result, yearMonth, ct);

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

    private async Task RebuildGatewayUsageAsync(
        PlatformDbContext db,
        RebuildResult result,
        string? yearMonth,
        CancellationToken ct)
    {
        var activities = await db.RuntimeActivities
            .AsNoTracking()
            .Where(activity =>
                activity.Component == RuntimeActivityComponents.LlmGateway
                && activity.Status == RuntimeActivityStatuses.Succeeded
                && (activity.Operation == "chat" || activity.Operation == "chat_stream"))
            .OrderBy(activity => activity.StartedAtUtc)
            .ThenBy(activity => activity.Id)
            .ToListAsync(ct);
        activities = activities
            .Where(activity => MatchesYearMonth(activity.StartedAtUtc, yearMonth))
            .ToList();
        result.GatewayActivitiesScanned = activities.Count;

        if (activities.Count == 0)
            return;

        // Rows written directly at the gateway are already authoritative. The
        // first rollout used a legacy "llm:" source id that cannot be joined to
        // RuntimeActivity by id, so retain and de-duplicate those rows by their
        // exact request identity instead of manufacturing a second fact.
        var directEventsQuery = db.LlmGatewayUsageEvents
            .AsNoTracking()
            .Where(existing => !existing.SourceId.StartsWith("runtime-activity:"));
        if (!string.IsNullOrWhiteSpace(yearMonth))
            directEventsQuery = directEventsQuery.Where(existing => existing.YearMonth == yearMonth);
        var directEventKeys = (await directEventsQuery.ToListAsync(ct))
            .Select(GatewayDedupKey.FromEvent)
            .ToHashSet();

        var prices = BuildPriceMap(llmConfigService?.GetAllModels() ?? []);
        var rebuilt = new List<LlmGatewayUsageEventEntity>();

        foreach (var activity in activities.Where(a => a.Operation == "chat"))
        {
            if (TryMapGatewayActivity(activity, out var mapped)
                && mapped.Usage is not null)
            {
                AddUnlessDirectlyRecorded(
                    rebuilt,
                    directEventKeys,
                    CreateGatewayEvent(activity, mapped, prices));
            }
            else
            {
                result.GatewayFactsSkipped++;
            }
        }

        var streamActivities = activities
            .Where(activity => activity.Operation == "chat_stream")
            .ToList();
        var workspaces = streamActivities
            .Select(activity => activity.WorkspaceId)
            .Where(workspace => !string.IsNullOrWhiteSpace(workspace))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var usageFrames = new List<GatewayUsageFrame>();
        foreach (var workspace in workspaces)
        {
            var workspaceEvents = await db.ConversationEvents
                .AsNoTracking()
                .Where(evt => evt.WorkspaceId == workspace && evt.Type == ConversationEventTypes.UsageRecorded)
                .OrderBy(evt => evt.Id)
                .ToListAsync(ct);
            foreach (var usageEvent in workspaceEvents.Where(evt => MatchesYearMonth(evt.OccurredAt, yearMonth)))
            {
                if (TryMapGatewayUsageEvent(usageEvent, out var frame))
                    usageFrames.Add(frame);
            }
        }

        // usage.recorded v2 payload carries invocationIndex (per-turn LLM invocation
        // counter), which is a precise pairing key: an ordered chat_stream activity at
        // position i maps to the usage event whose invocationIndex == i + 1. This
        // replaces the legacy index-based pairing against conversation_events "usage"
        // frames. Duplicate invocationIndex (multi-turn overlap) collapses to fewer
        // entries than activities, which fails the count check below and safely falls
        // back to self-contained facts instead of manufacturing a wrong pairing.
        var framesBySession = usageFrames
            .GroupBy(frame => (frame.WorkspaceId, frame.SessionId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(frame => frame.InvocationIndex)
                    .ToDictionary(
                        indexGroup => indexGroup.Key,
                        indexGroup => indexGroup.First().Usage));

        foreach (var group in streamActivities.GroupBy(
                     activity => (activity.WorkspaceId ?? string.Empty, activity.SessionId ?? string.Empty)))
        {
            var orderedActivities = group
                .OrderBy(activity => activity.StartedAtUtc)
                .ThenBy(activity => activity.Id)
                .ToList();
            framesBySession.TryGetValue(group.Key, out var framesByIndex);

            if (orderedActivities.Count > 0
                && framesByIndex is not null
                && orderedActivities.Count == framesByIndex.Count
                && Enumerable.Range(1, orderedActivities.Count).All(framesByIndex.ContainsKey))
            {
                for (var index = 0; index < orderedActivities.Count; index++)
                {
                    var activity = orderedActivities[index];
                    if (!TryMapGatewayActivity(activity, out var mapped)
                        || !framesByIndex.TryGetValue(index + 1, out var usage))
                    {
                        result.GatewayFactsSkipped++;
                        continue;
                    }

                    AddUnlessDirectlyRecorded(
                        rebuilt,
                        directEventKeys,
                        CreateGatewayEvent(
                            activity,
                            mapped with { Usage = usage },
                            prices));
                }
                continue;
            }

            logger.LogWarning(
                "[TokenUsageRebuild] Stream usage pairing mismatch workspace={Workspace} session={Session} activities={Activities} frames={Frames}; only self-contained activity facts will be rebuilt",
                group.Key.Item1,
                group.Key.Item2,
                orderedActivities.Count,
                framesByIndex?.Count ?? 0);
            foreach (var activity in orderedActivities)
            {
                if (TryMapGatewayActivity(activity, out var mapped)
                    && mapped.Usage is not null)
                {
                    AddUnlessDirectlyRecorded(
                        rebuilt,
                        directEventKeys,
                        CreateGatewayEvent(activity, mapped, prices));
                }
                else
                {
                    result.GatewayFactsSkipped++;
                }
            }
        }

        // Replace only facts we actually reconstructed. Direct gateway facts
        // and rows whose diagnostic activity has expired or failed to persist
        // must survive a rebuild.
        var rebuiltSourceIds = rebuilt
            .Select(rebuiltEvent => rebuiltEvent.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var existingQuery = db.LlmGatewayUsageEvents
            .Where(existing => existing.SourceId.StartsWith("runtime-activity:"));
        if (!string.IsNullOrWhiteSpace(yearMonth))
            existingQuery = existingQuery.Where(existing => existing.YearMonth == yearMonth);
        var existing = (await existingQuery.ToListAsync(ct))
            .Where(existingEvent => rebuiltSourceIds.Contains(existingEvent.SourceId))
            .ToList();
        if (existing.Count > 0)
        {
            db.LlmGatewayUsageEvents.RemoveRange(existing);
            result.GatewayEventsDeleted = existing.Count;
        }

        if (rebuilt.Count > 0)
        {
            db.LlmGatewayUsageEvents.AddRange(rebuilt);
            result.GatewayEventsCreated = rebuilt.Count;
        }

        if (existing.Count > 0 || rebuilt.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static void AddUnlessDirectlyRecorded(
        ICollection<LlmGatewayUsageEventEntity> rebuilt,
        IReadOnlySet<GatewayDedupKey> directEventKeys,
        LlmGatewayUsageEventEntity candidate)
    {
        if (!directEventKeys.Contains(GatewayDedupKey.FromEvent(candidate)))
            rebuilt.Add(candidate);
    }

    private static bool MatchesYearMonth(string timestamp, string? yearMonth)
        => string.IsNullOrWhiteSpace(yearMonth)
           || DateTimeOffset.TryParse(timestamp, out var parsed)
           && parsed.ToString("yyyy-MM") == yearMonth;

    private static bool TryMapGatewayActivity(
        RuntimeActivityEntity activity,
        out GatewayActivityFact fact)
    {
        fact = default!;
        if (!DateTimeOffset.TryParse(activity.StartedAtUtc, out var occurredAt)
            || string.IsNullOrWhiteSpace(activity.MetadataJson))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                activity.MetadataJson,
                JsonOpts);
            if (metadata is null
                || !metadata.TryGetValue("provider_id", out var providerId)
                || !metadata.TryGetValue("model", out var modelId)
                || string.IsNullOrWhiteSpace(providerId)
                || string.IsNullOrWhiteSpace(modelId))
            {
                return false;
            }

            metadata.TryGetValue("agent_template_id", out var agentTemplateId);
            fact = new GatewayActivityFact(
                providerId,
                modelId,
                agentTemplateId,
                occurredAt,
                TryParseUsage(metadata, out var usage) ? usage : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseUsage(
        IReadOnlyDictionary<string, string> metadata,
        out TokenUsageDto usage)
    {
        usage = default!;
        if (!TryReadInt(metadata, "prompt_tokens", out var prompt)
            || !TryReadInt(metadata, "completion_tokens", out var completion))
        {
            return false;
        }

        TryReadInt(metadata, "total_tokens", out var total);
        TryReadInt(metadata, "prompt_cache_hit_tokens", out var hit);
        TryReadInt(metadata, "prompt_cache_miss_tokens", out var miss);
        usage = new TokenUsageDto
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total > 0 ? total : prompt + completion,
            PromptCacheHitTokens = hit,
            PromptCacheMissTokens = miss,
        };
        return true;
    }

    private static bool TryParseUsage(string json, out TokenUsageDto usage)
    {
        try
        {
            usage = JsonSerializer.Deserialize<TokenUsageDto>(json, JsonOpts)!;
            return usage is not null;
        }
        catch (JsonException)
        {
            usage = default!;
            return false;
        }
    }

    /// <summary>
    /// 将 canonical usage.recorded v2 事实事件映射为网关流式 usage 帧。
    /// payload 形态（TurnExecutorAdapter.CreateUsageRecordedPayload）：
    /// { usage: {...}, providerId, profileId, modelId, role, invocationIndex }。
    /// 仅接受带嵌套 usage 对象且 invocationIndex &gt; 0 的事件；缺少任一者即视为不可配对。
    /// </summary>
    private static bool TryMapGatewayUsageEvent(
        ConversationEventEntity entity,
        out GatewayUsageFrame frame)
    {
        frame = default!;
        try
        {
            using var document = JsonDocument.Parse(entity.Payload);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("usage", out var usageElement)
                || usageElement.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("invocationIndex", out var invocationElement)
                || !invocationElement.TryGetInt32(out var invocationIndex)
                || invocationIndex <= 0)
            {
                return false;
            }

            var usage = JsonSerializer.Deserialize<TokenUsageDto>(
                usageElement.GetRawText(),
                JsonOpts);
            if (usage is null)
                return false;

            frame = new GatewayUsageFrame(
                entity.WorkspaceId,
                entity.ConversationId,
                invocationIndex,
                usage);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out int value)
    {
        value = 0;
        return metadata.TryGetValue(key, out var raw) && int.TryParse(raw, out value);
    }

    private LlmGatewayUsageEventEntity CreateGatewayEvent(
        RuntimeActivityEntity activity,
        GatewayActivityFact fact,
        IReadOnlyDictionary<string, TokenPrice> prices)
    {
        var price = prices.TryGetValue(PriceKey(fact.ProviderId, fact.ModelId), out var configured)
            ? configured
            : TokenPrice.Zero;
        var cacheHitPrice = price.CacheHitPricePer1MTokens > 0
            ? price.CacheHitPricePer1MTokens
            : price.InputPricePer1MTokens;
        var normalized = normalizer.Normalize(
            fact.Usage!,
            price.InputPricePer1MTokens,
            price.OutputPricePer1MTokens,
            cacheHitPrice);
        return new LlmGatewayUsageEventEntity
        {
            SourceId = $"runtime-activity:{activity.ActivityId}",
            Operation = activity.Operation,
            WorkspaceId = activity.WorkspaceId,
            SessionId = activity.SessionId,
            AgentTemplateId = fact.AgentTemplateId,
            ProviderId = fact.ProviderId,
            ModelId = fact.ModelId,
            OccurredAtUtc = fact.OccurredAtUtc,
            YearMonth = fact.OccurredAtUtc.ToString("yyyy-MM"),
            PromptTokens = normalized.PromptTokens,
            CompletionTokens = normalized.CompletionTokens,
            TotalTokens = normalized.TotalTokens,
            CacheHitTokens = normalized.CacheHitTokens,
            CacheMissTokens = normalized.CacheMissTokens,
            InputCost = normalized.InputCost,
            OutputCost = normalized.OutputCost,
            CacheHitCost = normalized.CacheHitCost,
            TotalCost = normalized.TotalCost,
            RawUsageJson = JsonSerializer.Serialize(fact.Usage, JsonOpts),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
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

    private sealed record GatewayActivityFact(
        string ProviderId,
        string ModelId,
        string? AgentTemplateId,
        DateTimeOffset OccurredAtUtc,
        TokenUsageDto? Usage);

    private sealed record GatewayUsageFrame(
        string WorkspaceId,
        string SessionId,
        int InvocationIndex,
        TokenUsageDto Usage);

    private sealed record GatewayDedupKey(
        string Operation,
        string WorkspaceId,
        string SessionId,
        string ProviderId,
        string ModelId,
        long OccurredAtUtcTicks,
        long PromptTokens,
        long CompletionTokens,
        long TotalTokens)
    {
        public static GatewayDedupKey FromEvent(LlmGatewayUsageEventEntity usageEvent)
            => new(
                usageEvent.Operation,
                usageEvent.WorkspaceId ?? string.Empty,
                usageEvent.SessionId ?? string.Empty,
                usageEvent.ProviderId,
                usageEvent.ModelId,
                usageEvent.OccurredAtUtc.UtcTicks,
                usageEvent.PromptTokens,
                usageEvent.CompletionTokens,
                usageEvent.TotalTokens);
    }

    private sealed record TokenPrice(
        decimal InputPricePer1MTokens,
        decimal OutputPricePer1MTokens,
        decimal CacheHitPricePer1MTokens)
    {
        public static TokenPrice Zero { get; } = new(0m, 0m, 0m);
    }
}
