using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-057 Phase 7: Conversation Projector。
/// 从 Event Log 按 checkpoint 重放并生成 ChatMessages 物化视图。
/// <para>
/// 阶段 C2b：消息物化、读模型摘要（turn summaries + read_state）与 projection checkpoint 在
/// **同一 SQLite 事务**提交；token usage / catalog / 向量索引等外部副作用在 commit 之后执行。
/// 提交失败时三类写入一起回滚，checkpoint 不会领先状态。
/// </para>
/// </summary>
public sealed class ConversationProjector(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<PlatformDbContext> dbFactory,
    IConversationEventStore eventStore,
    ChatTranscriptWriter transcriptWriter,
    ConversationReadModelProjectionStore readModelStore,
    ILogger<ConversationProjector> logger,
    TokenUsageRecorder? tokenUsageRecorder = null,
    ConversationCatalogWriter? catalogWriter = null)
{
    private const int ProjectionBatchSize = 200;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ProjectionResult> ProjectAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var checkpointInfo = await GetCheckpointAsync(db, conversationId, ct);
            // 旧 Core 可能只推进 checkpoint、不写读模型；从读模型最后应用位置补放尾部，
            // 否则新端点会一直读到陈旧的 activeTurn（用户实测 13:04 终态仍显示运行中）。
            var checkpoint = await readModelStore.ResolveResumeSequenceAsync(
                db,
                conversationId,
                checkpointInfo.ProjectedThrough,
                checkpointInfo.UpdatedAt,
                ct);
            var batch = await eventStore.ReadForwardAsync(
                conversationId, checkpoint, null, limit: ProjectionBatchSize, ct);

            if (batch.Events.Count == 0)
                return new ProjectionResult(0, batch.HasMore, null);

            var readModelInputs = new List<ConversationEventInput>(batch.Events.Count);
            var affectedTurnIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evt in batch.Events)
            {
                var input = ConversationReadModelEventMapper.Map(evt);
                readModelInputs.Add(input);
                if (!string.IsNullOrWhiteSpace(input.TurnId))
                    affectedTurnIds.Add(input.TurnId);
            }

            var lastSequence = batch.Events[^1].Sequence;
            var now = DateTimeOffset.UtcNow;
            var projectedCount = 0;
            var createdMessages = new List<ChatMessageEntity>(batch.Events.Count);

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    var state = await readModelStore.LoadForBatchAsync(
                        db, conversationId, checkpoint, affectedTurnIds, ct);
                    var reduction = ConversationReadModelReducer.Apply(
                        state, readModelInputs, ConversationReadModelLimits.Default);

                    foreach (var evt in batch.Events)
                    {
                        var (role, content, thinking, usage) = ExtractFields(evt);
                        if (content is null && usage is null)
                            continue;

                        var entity = await transcriptWriter.PersistMessageInTransactionAsync(
                            db,
                            sessionId: conversationId,
                            role: role,
                            content: content ?? "",
                            createdAt: evt.OccurredAt.ToUnixTimeMilliseconds(),
                            thinkingJson: thinking,
                            usageJson: usage,
                            workspaceId: evt.WorkspaceId,
                            messageId: evt.MessageId,
                            turnId: evt.TurnId,
                            commandId: evt.CommandId,
                            ct: ct);
                        if (entity is not null)
                        {
                            createdMessages.Add(entity);
                            projectedCount++;
                        }
                    }

                    // 写读模型摘要/read_state 与 checkpoint：与消息物化同一事务。
                    await readModelStore.PersistAsync(
                        db,
                        reduction.State,
                        reduction.State.Turns.Keys.ToArray(),
                        lastSequence,
                        now,
                        ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            }

            // ── commit 之后：外部副作用（不参与投影事务成败） ──
            foreach (var entity in createdMessages)
                transcriptWriter.ScheduleIndexing(entity);

            foreach (var evt in batch.Events)
            {
                if (evt.Type != ConversationEventTypes.UsageRecorded || tokenUsageRecorder is null)
                    continue;
                try
                {
                    await ProjectTokenUsageAsync(evt);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[ConversationProjector] Post-commit usage projection failed conv={ConvId} event={EventId}",
                        conversationId, evt.EventId);
                }
            }

            if (catalogWriter is not null)
            {
                foreach (var evt in batch.Events)
                {
                    try
                    {
                        await catalogWriter.UpsertCatalogRowAsync(evt, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "[ConversationProjector] Post-commit catalog projection failed conv={ConvId} event={EventId}",
                            conversationId, evt.EventId);
                    }
                }
            }

            if (projectedCount > 0)
            {
                logger.LogInformation(
                    "[ConversationProjector] Projected conv={ConvId} materialized={MaterializedCount} sourceEvents={SourceEventCount} checkpoint={Prev}->{Next}",
                    conversationId,
                    projectedCount,
                    batch.Events.Count,
                    checkpoint,
                    lastSequence);
            }
            else
            {
                logger.LogDebug(
                    "[ConversationProjector] Advanced conv={ConvId} sourceEvents={SourceEventCount} checkpoint={Prev}->{Next} without transcript materialization",
                    conversationId,
                    batch.Events.Count,
                    checkpoint,
                    lastSequence);
            }

            return new ProjectionResult(projectedCount, batch.HasMore, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ConversationProjector] Projection failed conv={ConvId}", conversationId);
            return new ProjectionResult(0, false, ex.Message);
        }
    }

    private async Task ProjectTokenUsageAsync(ConversationEvent evt)
    {
        if (tokenUsageRecorder is null)
            return;

        if (evt.Payload.ValueKind != JsonValueKind.Object
            || !evt.Payload.TryGetProperty("usage", out var usageElement)
            || usageElement.ValueKind != JsonValueKind.Object)
        {
            logger.LogDebug(
                "[ConversationProjector] Skip unattributed usage event={EventId} schema={SchemaVersion}",
                evt.EventId,
                evt.SchemaVersion);
            return;
        }

        var providerId = ExtractString(evt.Payload, "providerId");
        var modelId = ExtractString(evt.Payload, "modelId");
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            logger.LogWarning(
                "[ConversationProjector] Skip usage without immutable route event={EventId} run={RunId}",
                evt.EventId,
                evt.RunId);
            return;
        }

        var usage = JsonSerializer.Deserialize<TokenUsageDto>(
            usageElement.GetRawText(),
            JsonOpts);
        if (usage is null)
        {
            logger.LogWarning(
                "[ConversationProjector] Skip invalid usage payload event={EventId}",
                evt.EventId);
            return;
        }

        // 同一次 LLM 调用通常已由执行服务直记（ADR-043，携带 prefix snapshot，sourceId 含 traceId/round）。
        // 投影路径按 usage 指纹在时间窗内查重，只在直记缺失时补记，避免 TokenUsageEvents 双计归因。
        if (await HasDirectlyRecordedUsageAsync(evt, usage, providerId, modelId))
        {
            logger.LogDebug(
                "[ConversationProjector] Skip duplicated usage event={EventId} conversation={ConversationId}",
                evt.EventId,
                evt.ConversationId);
            return;
        }

        var parentSessionId = await ResolveParentSessionAsync(evt.ConversationId);
        var invocationIndex = evt.Payload.TryGetProperty("invocationIndex", out var invocationElement)
                              && invocationElement.TryGetInt32(out var parsedInvocationIndex)
            ? parsedInvocationIndex
            : 0;

        await tokenUsageRecorder.RecordAttributedRequiredAsync(
            usage,
            sourceType: "agent_llm",
            sourceId: evt.EventId,
            workspaceId: evt.WorkspaceId,
            sessionId: evt.ConversationId,
            providerId: providerId,
            modelId: modelId,
            attribution: CreateFallbackAttribution(
                evt.ConversationId,
                parentSessionId,
                invocationIndex),
            occurredAtUtc: evt.OccurredAt,
            prefixSnapshot: null);
    }

    internal static TokenUsageAttribution CreateFallbackAttribution(
        string conversationId,
        string? parentSessionId,
        int invocationIndex)
        => new()
        {
            ParentSessionId = parentSessionId,
            SubAgentId = string.IsNullOrWhiteSpace(parentSessionId)
                ? null
                : conversationId,
            TurnRound = invocationIndex > 0 ? invocationIndex - 1 : null,
            ToolCallCount = null,
        };

    private async Task<string?> ResolveParentSessionAsync(string conversationId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var sub = await db.SessionSubAgents
                .AsNoTracking()
                .Where(s => s.SubSessionId == conversationId)
                .Select(s => s.ParentSessionId)
                .FirstOrDefaultAsync();
            return sub;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查同一次 LLM 调用是否已被执行服务直记（sourceId=session:trace:round）。
    /// 指纹 = 会话 + 路由 + prompt/completion token 计数，容许 ±2 分钟投影延迟。
    /// DTO 缺 prompt 计数时无法构成可靠指纹，返回 false 以保持补记语义。
    /// </summary>
    private async Task<bool> HasDirectlyRecordedUsageAsync(
        ConversationEvent evt,
        TokenUsageDto usage,
        string providerId,
        string modelId)
    {
        if (usage.PromptTokens is not > 0)
            return false;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var windowStart = evt.OccurredAt.AddMinutes(-2);
            var windowEnd = evt.OccurredAt.AddMinutes(2);
            var candidates = await ApplyDirectUsageFingerprintCandidates(
                    db.Set<TokenUsageEventEntity>().AsNoTracking(),
                    evt.ConversationId,
                    providerId,
                    modelId,
                    usage)
                .OrderByDescending(item => item.Id)
                .Take(32)
                .Select(item => item.OccurredAtUtc)
                .ToListAsync();
            return candidates.Any(occurredAt => occurredAt >= windowStart && occurredAt <= windowEnd);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ConversationProjector] Usage fingerprint check failed event={EventId}, fallback to record",
                evt.EventId);
            return false;
        }
    }

    internal static IQueryable<TokenUsageEventEntity> ApplyDirectUsageFingerprintCandidates(
        IQueryable<TokenUsageEventEntity> source,
        string conversationId,
        string providerId,
        string modelId,
        TokenUsageDto usage)
    {
        var directSourcePrefix = conversationId + ":";
        var promptTokens = (long)(usage.PromptTokens ?? 0);
        var query = source.Where(e => e.SessionId == conversationId
            && e.ProviderId == providerId
            && e.ModelId == modelId
            && e.SourceType == "agent_llm"
            && e.SourceId.StartsWith(directSourcePrefix)
            && e.PromptTokens == promptTokens);

        // Do not embed a captured nullable comparison as
        // `(completion == null || column == completion)`. The SQLite EF provider
        // can leave that expression as `False || ...` and fail translation,
        // which made the projector fall back and double-record every usage row.
        if (usage.CompletionTokens is int completionTokens)
        {
            var completionTokenCount = (long)completionTokens;
            query = query.Where(e => e.CompletionTokens == completionTokenCount);
        }

        return query;
    }

    private static (string role, string? content, string? thinking, string? usage) ExtractFields(ConversationEvent evt)
    {
        return evt.Type switch
        {
            // ADR-057: User messages are NOT projected by the event projector.
            // They are written directly by the message ingestion path.
            // turn.accepted is a machine event — skip it.
            ConversationEventTypes.TurnAccepted => (
                "user", null, null, null),

            ConversationEventTypes.TurnCompleted => (
                "agent",
                ExtractString(evt.Payload, "reply"),
                null,
                ExtractUsageJson(evt.Payload)),

            // ADR-057: turn.failed is NOT projected as a fake text message.
            // Failure details are delivered via SSE events + turn state,
            // not via the ChatMessages table.
            ConversationEventTypes.TurnFailed => (
                "agent", null, null, null),

            _ => ("agent", null, null, null),
        };
    }

    private static async Task<(long ProjectedThrough, DateTimeOffset UpdatedAt)> GetCheckpointAsync(
        PlatformDbContext db,
        string conversationId,
        CancellationToken ct)
    {
        var checkpoint = await db.ConversationProjectionCheckpoints
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .Select(item => new { item.ProjectedThrough, item.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (checkpoint is null)
            return (0L, DateTimeOffset.MinValue);

        return DateTimeOffset.TryParse(checkpoint.UpdatedAt, out var updatedAt)
            ? (checkpoint.ProjectedThrough, updatedAt)
            : (checkpoint.ProjectedThrough, DateTimeOffset.MinValue);
    }

    private static string? ExtractString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ExtractUsageJson(JsonElement el)
        => el.TryGetProperty("usage", out var v) ? v.GetRawText() : null;
}

public sealed record ProjectionResult(
    int ProjectedCount,
    bool HasMore,
    string? Error
);
