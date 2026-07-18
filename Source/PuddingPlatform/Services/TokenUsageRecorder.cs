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
    private readonly ILlmConfigService? _llmConfigService;
    private readonly ISessionTimelineRecorder? _timelineRecorder;

    public TokenUsageRecorder(
        IServiceScopeFactory scopeFactory,
        TokenUsageNormalizer normalizer,
        ILogger<TokenUsageRecorder> logger,
        ITelemetryMetricSink? telemetrySink = null,
        ContextAssemblyStore? contextAssemblyStore = null,
        ILlmConfigService? llmConfigService = null,
        ISessionTimelineRecorder? timelineRecorder = null)
    {
        _scopeFactory = scopeFactory;
        _normalizer = normalizer;
        _logger = logger;
        _telemetrySink = telemetrySink;
        _contextAssemblyStore = contextAssemblyStore;
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
        DateTimeOffset? occurredAtUtc = null)
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
                occurredAtUtc);
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
            occurredAtUtc);

    private async Task RecordCoreAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot,
        DateTimeOffset? occurredAtUtc)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // 幂等检查
        var exists = await db.Set<TokenUsageEventEntity>()
            .AnyAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);

        if (exists)
        {
            _logger.LogDebug(
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
            occurredAtUtc.Value);

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
                PrefixVersion = resolvedPrefixSnapshot?.Version,
                PrefixHash = resolvedPrefixSnapshot?.PrefixHash,
                SystemPromptHash = resolvedPrefixSnapshot?.SystemPromptHash,
                ToolSpecHash = resolvedPrefixSnapshot?.ToolSpecHash,
                MemoryHash = resolvedPrefixSnapshot?.MemoryHash,
                FewShotHash = resolvedPrefixSnapshot?.FewShotHash,
                PrefixChangeReason = resolvedPrefixSnapshot?.PrefixChangeReason,
                PrefixMessageCount = resolvedPrefixSnapshot?.MessageCount,
                PrefixToolCount = resolvedPrefixSnapshot?.ToolCount,
                CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await RecordContextLayerMetricsAsync(
            db,
            normalized,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
            occurredAtUtc.Value);

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
            occurredAtUtc.Value);
    }

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
        DateTimeOffset occurredAtUtc)
    {
        if (_contextAssemblyStore is null
            || string.IsNullOrWhiteSpace(sessionId)
            || !_contextAssemblyStore.TryGet(sessionId, out var snapshot)
            || snapshot is null
            || snapshot.Layers.Count == 0)
        {
            return;
        }

        var exists = await db.ContextLayerMetricEvents
            .AnyAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);
        if (exists)
            return;

        var priorCandidates = await db.ContextLayerMetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync();
        var previousByLayer = priorCandidates
            .GroupBy(e => e.LayerName)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id).First().ContentHash);

        var hitRemaining = normalized.CacheHitTokens;
        var missRemaining = normalized.CacheMissTokens;
        long tokenOffset = 0;
        for (var i = 0; i < snapshot.Layers.Count; i++)
        {
            var layer = snapshot.Layers[i];
            var tokens = Math.Max(0, layer.TokenCount);
            var hit = Math.Min(tokens, hitRemaining);
            hitRemaining -= hit;
            var remainingTokens = tokens - hit;
            var miss = Math.Min(remainingTokens, missRemaining);
            missRemaining -= miss;
            var hash = ComputeLayerHash(layer);
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
                LayoutVersion = "layer-v1",
                LayerName = layer.LayerName,
                LayerOrder = i,
                LayerRole = ClassifyLayerRole(layer.LayerName),
                TokenCount = tokens,
                CharCount = layer.ContentPreview?.Length ?? 0,
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

    private static string ClassifyLayerRole(string layerName)
    {
        var upper = layerName.ToUpperInvariant();
        if (upper.Contains("STATIC") || upper.Contains("ENVIRONMENT") || upper.Contains("TOOLS") || upper.Contains("SKILLS"))
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
        if (upper.Contains("TOOLS"))
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

    private static string ClassifyPrefixChange(
        TokenUsageEventEntity previous,
        PromptPrefixSnapshot current)
    {
        if (!string.Equals(previous.ToolSpecHash, current.ToolSpecHash, StringComparison.Ordinal))
            return "tool_spec_changed";
        if (!string.Equals(previous.SystemPromptHash, current.SystemPromptHash, StringComparison.Ordinal))
            return "system_prompt_changed";
        if (!string.Equals(previous.MemoryHash, current.MemoryHash, StringComparison.Ordinal))
            return "memory_changed";
        if (!string.Equals(previous.FewShotHash, current.FewShotHash, StringComparison.Ordinal))
            return "few_shot_changed";
        return "prefix_hash_changed";
    }
}
