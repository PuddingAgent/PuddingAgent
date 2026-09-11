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
        var rawUsageJson = JsonSerializer.Serialize(usage, JsonOptions);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 幂等前置检查：命中 source 后必须比较「usage 内容 + 不可变身份」，
        // 相同才算 DuplicateSame；异内容经 Required 路径抛出 ConflictDifferent，
        // 不得静默接受同 source 的不同用量。
        var existing = await db.LlmGatewayUsageEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceId == sourceId, ct);
        if (existing is not null)
        {
            EnsureSameGatewayUsageFact(existing, sourceId, operation, workspaceId,
                sessionId, agentTemplateId, providerId, modelId, rawUsageJson);
            logger.LogDebug(
                "[LlmGatewayUsage] Skip duplicate source={SourceId}",
                sourceId);
            return;
        }

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
            RawUsageJson = rawUsageJson,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UsageWriteConflict.IsUniqueSourceConflict(ex))
        {
            // 唯一索引兜底：SQLite 错误码无法区分是本账本 source 约束还是无关
            // 约束冲突。回滚（context 释放即回滚）后用干净 context 回查目标
            // source，做与前置检查相同的比较；查不到目标 source 说明是无关唯一
            // 失败，原样向 Required 调用方传播，绝不按幂等成功吞掉。
            await using var verifyDb = await dbFactory.CreateDbContextAsync(ct);
            var raced = await verifyDb.LlmGatewayUsageEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.SourceId == sourceId, ct);
            if (raced is null)
            {
                logger.LogWarning(
                    "[LlmGatewayUsage] Unique constraint failure without target source={SourceId}; rethrowing",
                    sourceId);
                throw;
            }

            EnsureSameGatewayUsageFact(raced, sourceId, operation, workspaceId,
                sessionId, agentTemplateId, providerId, modelId, rawUsageJson);
            logger.LogDebug(
                "[LlmGatewayUsage] Skip duplicate source={SourceId} (unique index race)",
                sourceId);
            return;
        }

        logger.LogDebug(
            "[LlmGatewayUsage] Recorded source={SourceId} operation={Operation} provider={Provider} model={Model} tokens={Tokens}",
            sourceId,
            operation,
            providerId,
            modelId,
            normalized.TotalTokens);
    }

    /// <summary>
    /// 幂等身份比较（S01-A-R）：已有 source 行与本次请求必须具有相同的不可变
    /// 身份（operation/workspace/session/agentTemplate/provider/model）与相同的
    /// usage 内容（RawUsageJson，规范化前的原始用量指纹）。任一不同即视为
    /// ConflictDifferent，经 Required 路径抛出明确失败。写入时间等本地字段
    /// 不参与比较，避免合法重投被误判冲突。
    /// </summary>
    private static void EnsureSameGatewayUsageFact(
        LlmGatewayUsageEventEntity existing,
        string sourceId,
        string operation,
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        string providerId,
        string modelId,
        string rawUsageJson)
    {
        var sameIdentity =
            string.Equals(existing.Operation, operation, StringComparison.Ordinal)
            && string.Equals(existing.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(existing.AgentTemplateId, agentTemplateId, StringComparison.Ordinal)
            && string.Equals(existing.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(existing.ModelId, modelId, StringComparison.Ordinal);
        var sameContent = string.Equals(existing.RawUsageJson, rawUsageJson, StringComparison.Ordinal);
        if (!sameIdentity || !sameContent)
        {
            throw new InvalidOperationException(
                $"[LlmGatewayUsage] Source conflict {sourceId}: duplicate source id with a different usage payload or immutable identity.");
        }
    }
}
