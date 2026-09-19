using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PuddingPlatform.Services;

/// <summary>
/// Token 使用事件记录器（ADR-043）。
/// 将已归因的单次 LLM usage 写入 TokenUsageEventEntity，并增量更新月度聚合。
/// 计费与审计事实使用 required 语义；仅非权威遥测允许 best-effort。
/// </summary>
public class TokenUsageRecorder : ITokenUsageRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenUsageNormalizer _normalizer;
    private readonly ILogger<TokenUsageRecorder> _logger;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly ContextAssemblyStore? _contextAssemblyStore;
    private readonly ContextUsageSnapshotStore? _contextUsageSnapshotStore;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly ISessionTimelineRecorder? _timelineRecorder;

    public TokenUsageRecorder(
        IServiceScopeFactory scopeFactory,
        TokenUsageNormalizer normalizer,
        ILogger<TokenUsageRecorder> logger,
        ITelemetryMetricSink? telemetrySink = null,
        ContextAssemblyStore? contextAssemblyStore = null,
        ContextUsageSnapshotStore? contextUsageSnapshotStore = null,
        ILlmConfigService? llmConfigService = null,
        ISessionTimelineRecorder? timelineRecorder = null)
    {
        _scopeFactory = scopeFactory;
        _normalizer = normalizer;
        _logger = logger;
        _telemetrySink = telemetrySink;
        _contextAssemblyStore = contextAssemblyStore;
        _contextUsageSnapshotStore = contextUsageSnapshotStore;
        _llmConfigService = llmConfigService;
        _timelineRecorder = timelineRecorder;
    }
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
                PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null)
    {
        try
        {
            await RecordCoreAsync(
                usage,
                sourceType,
                sourceId,
                workspaceId,
                sessionId,
                providerId,
                modelId,
                                prefixSnapshot,
                occurredAtUtc,
                parentSessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TokenUsageRecorder] Failed to record token usage source={SourceType}/{SourceId}",
                sourceType, sourceId);
        }
    }

    /// <summary>
    /// 供持久投影器和 LLM 调用拥有方使用。写入失败时向上抛出，禁止工作流静默丢失用量事实。
    /// </summary>
    public Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
                PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null)
        => RecordCoreAsync(
            usage,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
                        prefixSnapshot,
            occurredAtUtc,
            parentSessionId);

    public Task RecordAttributedRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        TokenUsageAttribution attribution,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null)
        => RecordCoreAsync(
            usage,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
            prefixSnapshot,
            occurredAtUtc,
            attribution.ParentSessionId,
            attribution);

    private async Task RecordCoreAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
                PromptPrefixSnapshot? prefixSnapshot,
        DateTimeOffset? occurredAtUtc,
        string? parentSessionId = null,
        TokenUsageAttribution? attribution = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var occurredAt = (occurredAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var yearMonth = occurredAt.ToString("yyyy-MM");

        // S01-B：请求级归因。只消费调用方在请求准备阶段冻结的 RequestContextAttribution；
        // 缺失时才回退到“写入时刻的 session 最新快照”，并显式标注 provenance。
        var requestContext = ResolveRequestContext(attribution, sessionId);

        // 查询价格配置
        var inputPrice = 0m;
        var outputPrice = 0m;
        var cacheHitPrice = 0m;

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var models = _llmConfigService?.GetAllModels() ?? [];
            var priceConfig = models.FirstOrDefault(m =>
                string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(providerId)
                    || string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
                ?? models
                    .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() == 1)
                    .Select(g => g.First())
                    .FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

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
        var normalized = _normalizer.Normalize(usage, inputPrice, outputPrice, cacheHitPrice);

        var resolvedPrefixSnapshot = await ResolvePrefixChangeReasonAsync(
            db,
            sessionId,
            prefixSnapshot,
            occurredAt);

        // 写入明细账本
        var rawJson = System.Text.Json.JsonSerializer.Serialize(usage, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var usageEvent = new TokenUsageEventEntity
        {
                SourceType = sourceType,
                SourceId = sourceId,
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                ProviderId = providerId,
                ModelId = modelId,
                OccurredAtUtc = occurredAt,
                YearMonth = yearMonth,
                PromptTokens = normalized.PromptTokens,
                CompletionTokens = normalized.CompletionTokens,
                TotalTokens = normalized.TotalTokens,
                CacheHitTokens = normalized.CacheHitTokens,
                CacheMissTokens = normalized.CacheMissTokens,
                                CacheEligibleTokens = normalized.CacheEligibleTokens,
                MessageTokens = requestContext?.MessageTokens,
                ToolDefinitionTokens = requestContext?.ToolDefinitionTokens,
                SystemMessageTokens = requestContext?.SystemMessageTokens,
                HistoryMessageTokens = requestContext?.HistoryMessageTokens,
                SystemPromptTokens = requestContext?.SystemPromptTokens,
                CompactionSummaryTokens = requestContext?.CompactionSummaryTokens,
                ConversationTokens = requestContext?.ConversationTokens,
                ToolResultTokens = requestContext?.ToolResultTokens,
                ReasoningTokens = requestContext?.ReasoningTokens,
                SystemMessageEntropy = requestContext?.SystemMessageEntropy,
                HistoryMessageEntropy = requestContext?.HistoryMessageEntropy,
                ToolDefinitionEntropy = requestContext?.ToolDefinitionEntropy,
                CacheHitRate = normalized.CacheHitRate,
                InputCost = normalized.InputCost,
                OutputCost = normalized.OutputCost,
                CacheHitCost = normalized.CacheHitCost,
                TotalCost = normalized.TotalCost,
                RawUsageJson = rawJson,
                PrefixVersion = resolvedPrefixSnapshot?.Version,
                PrefixHash = resolvedPrefixSnapshot?.PrefixHash,
                SystemPromptHash = resolvedPrefixSnapshot?.SystemPromptHash,
                ToolSpecHash = resolvedPrefixSnapshot?.ToolSpecHash,
                MemoryHash = resolvedPrefixSnapshot?.MemoryHash,
                FewShotHash = resolvedPrefixSnapshot?.FewShotHash,
                PrefixChangeReason = resolvedPrefixSnapshot?.PrefixChangeReason,
                PrefixMessageCount = resolvedPrefixSnapshot?.MessageCount,
                                PrefixToolCount = resolvedPrefixSnapshot?.ToolCount,
                ParentSessionId = attribution?.ParentSessionId ?? parentSessionId,
                TurnRound = attribution?.TurnRound,
                ToolCallCount = attribution?.ToolCallCount,
                ToolNames = SerializeToolNames(attribution?.ToolNames),
                SubAgentId = attribution?.SubAgentId
                    ?? (!string.IsNullOrWhiteSpace(parentSessionId) ? sessionId : null),
                CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        // BEGIN IMMEDIATE：usage 明细与月度聚合处于同一写事务，且写锁从事务
        // 开始即持有。并发 recorder 被 SQLite 序列化——后到者必须等前者提交后
        // 才能读到聚合新值，消除「两个连接同读 RequestCount=100、各写回 101」
        // 的丢失更新。金额仍按既有 decimal 语义在 CLR 内运算，不经 SQLite
        // 数值转换，精度不回退。
        await using var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);

        // 幂等检查（写锁内读取是最终保证；unique 索引作为兜底）。
        var existing = await db.Set<TokenUsageEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);

        if (existing is not null)
        {
            if (!IsSameUsageFact(existing, usageEvent))
            {
                // ConflictDifferent：同一 source 身份，但 usage 内容或不可变身份
                //（workspace/session/provider/model）不同。冲突上报，不得静默覆盖或双计。
                throw new InvalidOperationException(
                    $"[TokenUsageRecorder] Source conflict {sourceType}/{sourceId}: duplicate source id with a different usage payload or immutable identity.");
            }

            db.Database.RollbackTransaction();
            _logger.LogDebug(
                "[TokenUsageRecorder] Skip duplicate source={SourceType}/{SourceId}",
                sourceType, sourceId);
            return;
        }

        db.Set<TokenUsageEventEntity>().Add(usageEvent);

        await RecordContextLayerMetricsAsync(
            db,
            normalized,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
            occurredAt,
            requestContext);

        // 更新月度聚合（事务内读改写，写者串行）
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

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (UsageWriteConflict.IsUniqueSourceConflict(ex))
        {
            // 兜底：SQLite 错误码无法区分是本账本 source 约束还是无关约束冲突
            //（SaveChanges 同时写聚合与 context layer 数据）。事务回滚保证聚合
            // 零残留，再用干净 context 回查目标 source，做与前置检查相同的比较：
            // 查得到且内容身份一致才允许按幂等重复成功；查不到目标 source 说明
            // 是无关唯一失败，原样向 Required 调用方传播，绝不吞成成功。
            db.Database.RollbackTransaction();

            using var verifyScope = _scopeFactory.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var raced = await verifyDb.Set<TokenUsageEventEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);

            if (raced is null)
            {
                _logger.LogWarning(
                    "[TokenUsageRecorder] Unique constraint failure without target source={SourceType}/{SourceId}; rethrowing",
                    sourceType, sourceId);
                throw;
            }

            if (!IsSameUsageFact(raced, usageEvent))
            {
                throw new InvalidOperationException(
                    $"[TokenUsageRecorder] Source conflict {sourceType}/{sourceId}: duplicate source id with a different usage payload or immutable identity.",
                    ex);
            }

            _logger.LogWarning(
                "[TokenUsageRecorder] Duplicate insert raced for source={SourceType}/{SourceId}; skipped",
                sourceType, sourceId);
            return;
        }

        await tx.CommitAsync();

        await InvalidateClosedDayAggregateAsync(scope, occurredAt, sourceType, sourceId);

        _logger.LogDebug(
            "[TokenUsageRecorder] Recorded source={SourceType}/{SourceId} provider={Provider} model={Model} cost={Cost}",
            sourceType, sourceId, providerIdVal, modelIdVal, normalized.TotalCost);

        await RecordTelemetryAsync(
            normalized,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerIdVal,
            modelIdVal,
            resolvedPrefixSnapshot,
            occurredAt);
    }

    /// <summary>
    /// 幂等身份比较（S01-A-R）：source 键命中后，已有行与本次写入的 usage 内容
    ///（RawUsageJson）和不可变身份（workspace/session/provider/model）必须全部
    /// 一致才允许判为 DuplicateSame。CreatedAt、写入时间等本地字段与 context
    /// layer 派生字段不参与比较，避免合法重投被误判冲突。
    /// </summary>
    private static bool IsSameUsageFact(TokenUsageEventEntity existing, TokenUsageEventEntity incoming)
        => string.Equals(existing.RawUsageJson, incoming.RawUsageJson, StringComparison.Ordinal)
           && string.Equals(existing.WorkspaceId, incoming.WorkspaceId, StringComparison.Ordinal)
           && string.Equals(existing.SessionId, incoming.SessionId, StringComparison.Ordinal)
           && string.Equals(existing.ProviderId, incoming.ProviderId, StringComparison.Ordinal)
           && string.Equals(existing.ModelId, incoming.ModelId, StringComparison.Ordinal);

    private async Task RecordTelemetryAsync(
        TokenUsageNormalizer.NormalizedUsage normalized,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string providerId,
        string modelId,
        PromptPrefixSnapshot? prefixSnapshot,
        DateTimeOffset occurredAtUtc)
    {
        try
        {
            var trace = RuntimeTraceContext.CreateNew(
                sessionId: sessionId,
                workspaceId: workspaceId);

            // 1. 写入 telemetry_metric_events (token.usage)
            if (_telemetrySink is not null)
            {
                await _telemetrySink.RecordAsync(new TelemetryMetric
                {
                    Trace = trace,
                    Source = "backend",
                    Category = TelemetryMetricCategories.TokenUsage,
                    Name = "token.usage",
                    Status = TelemetryMetricStatuses.Recorded,
                    OccurredAtUtc = occurredAtUtc,
                    CountValue = 1,
                    NumericValue = normalized.TotalTokens,
                    Unit = "tokens",
                    Summary = "Token usage recorded.",
                    Dimensions = new Dictionary<string, string>
                    {
                        ["source_type"] = sourceType,
                        ["source_id"] = sourceId,
                        ["provider_id"] = providerId,
                        ["model_id"] = modelId,
                        ["prompt_tokens"] = normalized.PromptTokens.ToString(),
                        ["completion_tokens"] = normalized.CompletionTokens.ToString(),
                        ["total_tokens"] = normalized.TotalTokens.ToString(),
                        ["cache_hit_tokens"] = normalized.CacheHitTokens.ToString(),
                        ["cache_miss_tokens"] = normalized.CacheMissTokens.ToString(),
                        ["cache_eligible_tokens"] = normalized.CacheEligibleTokens.ToString(),
                        ["cache_hit_rate"] = normalized.CacheHitRate?.ToString("0.######") ?? "",
                        ["total_cost"] = normalized.TotalCost.ToString("0.##########"),
                        ["prefix_hash"] = prefixSnapshot?.PrefixHash ?? "",
                        ["prefix_change_reason"] = prefixSnapshot?.PrefixChangeReason ?? "",
                    },
                });

                // 2. 写入专用的 llm.cache.hit_rate 指标（便于按维度聚合）
                if (normalized.CacheHitRate.HasValue)
                {
                    _ = _telemetrySink.RecordAsync(new TelemetryMetric
                    {
                        Trace = trace,
                        Source = "backend",
                        Category = TelemetryMetricCategories.TokenUsage,
                        Name = "llm.cache.hit_rate",
                        Status = TelemetryMetricStatuses.Recorded,
                        OccurredAtUtc = occurredAtUtc,
                        CountValue = 1,
                        NumericValue = normalized.CacheHitTokens,
                        Unit = "tokens",
                        Summary = $"Cache hit rate: {normalized.CacheHitRate.Value:P1}",
                        Dimensions = new Dictionary<string, string>
                        {
                            ["workspace_id"] = workspaceId ?? "",
                            ["session_id"] = sessionId ?? "",
                            ["provider_id"] = providerId,
                            ["model_id"] = modelId,
                            ["request_kind"] = sourceType,
                            ["hit_tokens"] = normalized.CacheHitTokens.ToString(),
                            ["miss_tokens"] = normalized.CacheMissTokens.ToString(),
                            ["total_prompt_tokens"] = normalized.PromptTokens.ToString(),
                            ["hit_rate"] = normalized.CacheHitRate.Value.ToString("0.######"),
                        },
                    });
                }
            }

            // 3. 写入 Session Timeline (diagnostics JSONL)
            if (_timelineRecorder is not null)
            {
                _ = _timelineRecorder.RecordAsync(new SessionTimelineRecord
                {
                    Trace = trace,
                    Component = "llm",
                    Stage = "usage",
                    Operation = "llm.usage.cache",
                    Status = "recorded",
                    RecordedAtUtc = occurredAtUtc,
                    Severity = "info",
                    Summary = $"cache_hit={normalized.CacheHitTokens} cache_miss={normalized.CacheMissTokens} rate={normalized.CacheHitRate:P1}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["sessionId"] = sessionId ?? "",
                        ["providerId"] = providerId,
                        ["modelId"] = modelId,
                        ["promptCacheHitTokens"] = normalized.CacheHitTokens.ToString(),
                        ["promptCacheMissTokens"] = normalized.CacheMissTokens.ToString(),
                        ["cacheHitRate"] = normalized.CacheHitRate?.ToString("0.######") ?? "",
                        ["totalPromptTokens"] = normalized.PromptTokens.ToString(),
                        ["completionTokens"] = normalized.CompletionTokens.ToString(),
                    },
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TokenUsageRecorder] Failed to record telemetry metric source={SourceType}/{SourceId}", sourceType, sourceId);
        }
    }

    private async Task RecordContextLayerMetricsAsync(
        PlatformDbContext db,
        TokenUsageNormalizer.NormalizedUsage normalized,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        DateTimeOffset occurredAtUtc,
        RequestContextAttribution? requestContext)
    {
        if (requestContext is null)
        {
            return;
        }

        var hasToolDefinitionLayer = requestContext.ToolDefinitionTokens is > 0;
        if (requestContext.Layers.Count == 0 && !hasToolDefinitionLayer)
            return;

        var exists = await db.ContextLayerMetricEvents
            .AnyAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);
        if (exists)
            return;

        var metricLayers = requestContext.Layers
            .Select(layer => new ContextLayerInfo
            {
                LayerName = layer.LayerName,
                TokenCount = layer.TokenCount,
                ContentPreview = layer.ContentPreview,
                FullContent = layer.FullContent,
            })
            .ToList();
        if (hasToolDefinitionLayer)
        {
            var toolHash = requestContext.ToolDefinitionHash;
            var toolPreview = string.IsNullOrWhiteSpace(toolHash)
                ? $"tool_count={requestContext.ToolCount}"
                : $"tool_count={requestContext.ToolCount};tool_hash={toolHash}";
            var insertionIndex = metricLayers.FindIndex(
                layer => !layer.LayerName.StartsWith("L0-", StringComparison.OrdinalIgnoreCase));
            if (insertionIndex < 0)
                insertionIndex = metricLayers.Count;
            metricLayers.Insert(insertionIndex, new ContextLayerInfo
            {
                LayerName = "L1-TOOL-DEFINITIONS",
                TokenCount = requestContext.ToolDefinitionTokens!.Value,
                ContentPreview = toolPreview,
            });
        }

        // One covering-index endpoint per current layer, not the entire session's
        // historical ledger (hundreds of thousands of entities for long-lived chats).
        var previousByLayer = new Dictionary<string, string?>();
        foreach (var layerName in metricLayers.Select(layer => layer.LayerName).Distinct(StringComparer.Ordinal))
            previousByLayer[layerName] = await ReadPreviousLayerHashAsync(db, sessionId, layerName);

        var hitRemaining = normalized.CacheHitTokens;
        var missRemaining = normalized.CacheMissTokens;
        long tokenOffset = 0;
        for (var i = 0; i < metricLayers.Count; i++)
        {
            var layer = metricLayers[i];
            var tokens = Math.Max(0, layer.TokenCount);
            var layerContent = layer.FullContent ?? layer.ContentPreview ?? string.Empty;
            var compression = layer.LayerName.Equals("L1-TOOL-DEFINITIONS", StringComparison.OrdinalIgnoreCase)
                              && requestContext.ToolDefinitionTokens is > 0
                ? new EntropyProbe.GzipMetrics(
                    requestContext.ToolDefinitionUtf8Bytes,
                    requestContext.ToolDefinitionGzipBytes,
                    requestContext.ToolDefinitionEntropy ?? 1.0)
                : EntropyProbe.Measure(layerContent);
            var hit = Math.Min(tokens, hitRemaining);
            hitRemaining -= hit;
            var remainingTokens = tokens - hit;
            var miss = Math.Min(remainingTokens, missRemaining);
            missRemaining -= miss;
            var hash = layer.LayerName.Equals("L1-TOOL-DEFINITIONS", StringComparison.OrdinalIgnoreCase)
                       && !string.IsNullOrWhiteSpace(requestContext.ToolDefinitionHash)
                ? requestContext.ToolDefinitionHash!
                : ComputeLayerHash(layer);
            previousByLayer.TryGetValue(layer.LayerName, out var previousHash);
            var isChanged = !string.IsNullOrWhiteSpace(previousHash)
                && !string.Equals(previousHash, hash, StringComparison.Ordinal);

            db.ContextLayerMetricEvents.Add(new ContextLayerMetricEventEntity
            {
                SourceType = sourceType,
                SourceId = sourceId,
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                ProviderId = providerId,
                ModelId = modelId,
                OccurredAtUtc = occurredAtUtc,
                AssemblerVersion = "context-v1",
                LayoutVersion = "layer-v2",
                LayerName = layer.LayerName,
                LayerOrder = i,
                LayerRole = ClassifyLayerRole(layer.LayerName),
                TokenCount = tokens,
                CharCount = layerContent.Length,
                RawUtf8Bytes = compression.RawUtf8Bytes,
                GzipBytes = compression.GzipBytes,
                GzipRatio = compression.GzipRatio,
                ContentHash = hash,
                PreviousHash = previousHash,
                IsChanged = isChanged,
                ChangeReason = isChanged ? ClassifyLayerChange(layer.LayerName) : null,
                StartsAtToken = tokenOffset,
                EndsAtToken = tokenOffset + tokens,
                IsCacheEligible = true,
                EstimatedCacheHitTokens = hit,
                EstimatedCacheMissTokens = miss,
                EstimatedCacheHitRate = (hit + miss) > 0 ? (double)hit / (hit + miss) : null,
                Confidence = "estimated",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            tokenOffset += tokens;
        }

    }

    internal const string PreviousLayerHashSql = """
        SELECT content_hash AS Value
        FROM context_layer_metric_events INDEXED BY IX_context_layer_metric_events_session_layer_time_id_hash
        WHERE session_id = {0} AND layer_name = {1}
        ORDER BY occurred_at_utc DESC, id DESC LIMIT 1
        """;

    internal static async Task<string?> ReadPreviousLayerHashAsync(
        PlatformDbContext db, string sessionId, string layerName)
    {
        // occurred_at_utc is the UTC ledger timestamp in the SQLite provider's
        // canonical format. Sort in SQL (EF cannot OrderBy DateTimeOffset on SQLite),
        // preserving event-time then Id ordering, including late-arriving facts.
        var hashes = await db.Database.SqlQueryRaw<string>(PreviousLayerHashSql, sessionId, layerName).ToListAsync();
        return hashes.Count == 0 ? null : hashes[0];
    }

    private static string ClassifyLayerRole(string layerName)
    {
        var upper = layerName.ToUpperInvariant();
        if (upper.Contains("STATIC") || upper.Contains("ENVIRONMENT") || upper.Contains("TOOL") || upper.Contains("SKILLS"))
            return "stable_prefix";
        if (upper.Contains("RECENT") || upper.Contains("CURRENT"))
            return "dynamic_history";
        if (upper.Contains("PINNED") || upper.Contains("RECALLED") || upper.Contains("USER"))
            return "memory_context";
        return "runtime_context";
    }

    private static string ClassifyLayerChange(string layerName)
    {
        var upper = layerName.ToUpperInvariant();
        if (upper.Contains("TOOL"))
            return "tool_spec_changed";
        if (upper.Contains("MEMORY") || upper.Contains("PINNED") || upper.Contains("RECALLED"))
            return "memory_changed";
        if (upper.Contains("RECENT") || upper.Contains("CURRENT"))
            return "history_changed";
        return "layer_hash_changed";
    }

    private static string ComputeLayerHash(ContextLayerInfo layer)
    {
        var text = $"{layer.LayerName}\n{layer.TokenCount}\n{layer.ContentPreview}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    private static async Task<PromptPrefixSnapshot?> ResolvePrefixChangeReasonAsync(
        PlatformDbContext db,
        string? sessionId,
        PromptPrefixSnapshot? current,
        DateTimeOffset occurredAtUtc)
    {
        if (current is null
            || !string.IsNullOrWhiteSpace(current.PrefixChangeReason)
            || string.IsNullOrWhiteSpace(sessionId))
        {
            return current;
        }

        var previous = await db.Set<TokenUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.PrefixHash != null)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();

        if (previous is null
            || string.Equals(previous.PrefixHash, current.PrefixHash, StringComparison.Ordinal))
        {
            return current;
        }

        var reason = ClassifyPrefixChange(previous, current);
        return current with
        {
            PrefixChangeReason = reason,
            CreatedAtUtc = occurredAtUtc,
        };
    }

    /// <summary>
    /// S01-B：解析本次落账使用的请求级上下文归因。
    /// 调用方在请求准备阶段冻结的归因优先；缺失时只能回退读 session 最新快照，
    /// 并把来源标为 session_latest_fallback —— 该来源不得当作请求级事实，
    /// 也不允许用它填满报表。
    /// </summary>
    private RequestContextAttribution? ResolveRequestContext(
        TokenUsageAttribution? attribution,
        string? sessionId)
    {
        if (attribution?.Context is not null)
            return attribution.Context;

        var captured = RequestContextAttribution.Capture(
            _contextAssemblyStore,
            _contextUsageSnapshotStore,
            sessionId);
        if (captured is null)
        {
            _logger.LogDebug(
                "[TokenUsageRecorder] Context attribution missing for session={SessionId}; recorded as unknown",
                sessionId);
            return null;
        }

        _logger.LogDebug(
            "[TokenUsageRecorder] Context attribution fell back to session-latest snapshot session={SessionId}",
            sessionId);
        return captured with { Source = RequestContextAttributionSources.SessionLatestFallback };
    }

    /// <summary>
    /// S01-B：迟到补录作用于已构建的闭日聚合。账本事实提交成功后，若该事实归属的 UTC 日
    /// 不是今天，则失效该日缓存，下一次查询按账本重算。失效失败不回滚已提交的账本事实。
    /// </summary>
    private async Task InvalidateClosedDayAggregateAsync(
        IServiceScope scope,
        DateTimeOffset occurredAtUtc,
        string sourceType,
        string sourceId)
    {
        var occurredDay = occurredAtUtc.UtcDateTime.Date;
        if (occurredDay >= DateTime.UtcNow.Date)
            return;

        try
        {
            var aggregateService = scope.ServiceProvider
                .GetService<TokenUsageDailyAggregateService>();
            if (aggregateService is null)
                return;

            await aggregateService.InvalidateDayAsync(occurredAtUtc);
            _logger.LogInformation(
                "[TokenUsageRecorder] Late-arriving usage invalidated closed-day aggregate day={Day} source={SourceType}/{SourceId}",
                occurredDay.ToString("yyyy-MM-dd"),
                sourceType,
                sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TokenUsageRecorder] Failed to invalidate closed-day aggregate day={Day} source={SourceType}/{SourceId}",
                occurredDay.ToString("yyyy-MM-dd"),
                sourceType,
                sourceId);
        }
    }

    private static string? SerializeToolNames(IReadOnlyList<string>? toolNames)
    {
        if (toolNames is not { Count: > 0 })
            return null;

        var joined = string.Join(",", toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return joined.Length switch
        {
            0 => null,
            <= 512 => joined,
            _ => joined[..512],
        };
    }

    private static string ClassifyPrefixChange(
        TokenUsageEventEntity previous,
        PromptPrefixSnapshot current)
    {
        if (!string.Equals(previous.PrefixVersion, current.Version, StringComparison.Ordinal))
            return PrefixChangeReasons.SerializationVersionChanged;
        if (!string.Equals(previous.ToolSpecHash, current.ToolSpecHash, StringComparison.Ordinal))
            return "tool_spec_changed";
        if (!string.Equals(previous.SystemPromptHash, current.SystemPromptHash, StringComparison.Ordinal))
            return "system_prompt_changed";
        if (!string.Equals(previous.MemoryHash, current.MemoryHash, StringComparison.Ordinal))
            return "memory_changed";
        if (!string.Equals(previous.FewShotHash, current.FewShotHash, StringComparison.Ordinal))
            return "few_shot_changed";
        // prefix-v2 includes the first non-system history anchor. Once all separately
        // persisted header hashes are stable, the remaining change is a history-head epoch.
        return PrefixChangeReasons.HistoryAnchorChanged;
    }
}
