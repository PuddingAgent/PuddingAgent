using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Required, idempotent writer for the provider/gateway billing ledger.
/// </summary>
public sealed class LlmGatewayUsageRecorder(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TokenUsageNormalizer normalizer,
    ILlmConfigService llmConfigService,
    ILogger<LlmGatewayUsageRecorder> logger) : ILlmGatewayUsageRecorder
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceId,
        string operation,
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        string providerId,
        string modelId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.LlmGatewayUsageEvents.AnyAsync(e => e.SourceId == sourceId, ct))
            return;

        var model = llmConfigService.GetAllModels().FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        var inputPrice = model?.InputPricePer1MTokens ?? 0m;
        var outputPrice = model?.OutputPricePer1MTokens ?? 0m;
        var cacheHitPrice = model is { CacheHitPricePer1MTokens: > 0 }
            ? model.CacheHitPricePer1MTokens
            : inputPrice;
        var normalized = normalizer.Normalize(
            usage,
            inputPrice,
            outputPrice,
            cacheHitPrice);

        db.LlmGatewayUsageEvents.Add(new LlmGatewayUsageEventEntity
        {
            SourceId = sourceId,
            Operation = operation,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentTemplateId = agentTemplateId,
            ProviderId = providerId,
            ModelId = modelId,
            OccurredAtUtc = occurredAtUtc,
            YearMonth = occurredAtUtc.ToString("yyyy-MM"),
            PromptTokens = normalized.PromptTokens,
            CompletionTokens = normalized.CompletionTokens,
            TotalTokens = normalized.TotalTokens,
            CacheHitTokens = normalized.CacheHitTokens,
            CacheMissTokens = normalized.CacheMissTokens,
            InputCost = normalized.InputCost,
            OutputCost = normalized.OutputCost,
            CacheHitCost = normalized.CacheHitCost,
            TotalCost = normalized.TotalCost,
            RawUsageJson = JsonSerializer.Serialize(usage, JsonOptions),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        logger.LogDebug(
            "[LlmGatewayUsage] Recorded source={SourceId} operation={Operation} provider={Provider} model={Model} tokens={Tokens}",
            sourceId,
            operation,
            providerId,
            modelId,
            normalized.TotalTokens);
    }
}
