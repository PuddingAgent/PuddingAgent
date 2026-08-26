using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-057 Phase 7: Conversation Projector。
/// 从 Event Log 按 checkpoint 重放并生成 ChatMessages 物化视图。
/// </summary>
public sealed class ConversationProjector(
    IServiceScopeFactory scopeFactory,
    IConversationEventStore eventStore,
    IChatTranscriptWriter transcriptWriter,
    TokenUsageRecorder tokenUsageRecorder,
    ConversationCatalogWriter catalogWriter,
    ILogger<ConversationProjector> logger)
{
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
            var checkpoint = await GetCheckpointAsync(conversationId, ct);
            var batch = await eventStore.ReadForwardAsync(
                conversationId, checkpoint, null, limit: 200, ct);

            var projectedCount = 0;

            foreach (var evt in batch.Events)
            {
                if (evt.Type == ConversationEventTypes.UsageRecorded)
                {
                    await ProjectTokenUsageAsync(evt);
                }

                var (role, content, thinking, usage) = ExtractFields(evt);
                if (content is not null || usage is not null)
                {
                    await transcriptWriter.PersistMessageAsync(
                        sessionId: conversationId,
                        role: role,
                        content: content ?? "",
                        createdAt: evt.OccurredAt.ToUnixTimeMilliseconds(),
                        thinkingJson: thinking,
                        usageJson: usage,
                        workspaceId: evt.WorkspaceId,
                        agentInstanceId: null,
                        agentTemplateId: null,
                        messageId: evt.MessageId,
                        turnId: evt.TurnId,
                        commandId: evt.CommandId,
                        ct: ct);
                    projectedCount++;
                }

                // ADR-057 P0-4f 第⑤步第⑤小步 C2: 为每个事件 UPSERT 一行 conversation_catalog 物化投影。
                // 不改 ExtractFields / ProjectTokenUsageAsync / PersistMessageAsync / SetCheckpoint 的既有逻辑。
                await catalogWriter.UpsertCatalogRowAsync(evt, ct);
            }

            if (batch.Events.Count > 0)
            {
                var lastSeq = batch.Events[^1].Sequence;
                await SetCheckpointAsync(conversationId, lastSeq, ct);
            }

            logger.LogInformation(
                "[ConversationProjector] Projected conv={ConvId} events={Count} checkpoint={Prev}->{Next}",
                conversationId, projectedCount, checkpoint,
                batch.Events.Count > 0 ? batch.Events[^1].Sequence : checkpoint);

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
            var directSourcePrefix = evt.ConversationId + ":";
            return await db.Set<TokenUsageEventEntity>()
                .AsNoTracking()
                .AnyAsync(e => e.SessionId == evt.ConversationId
                    && e.ProviderId == providerId
                    && e.ModelId == modelId
                    && e.SourceType == "agent_llm"
                    && e.SourceId.StartsWith(directSourcePrefix)
                    && e.OccurredAtUtc >= windowStart
                    && e.OccurredAtUtc <= windowEnd
                    && e.PromptTokens == usage.PromptTokens
                    && (usage.CompletionTokens == null || e.CompletionTokens == usage.CompletionTokens));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ConversationProjector] Usage fingerprint check failed event={EventId}, fallback to record",
                evt.EventId);
            return false;
        }
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

    private async Task<long> GetCheckpointAsync(string conversationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT projected_through FROM conversation_projection_checkpoints
            WHERE conversation_id = @cid";
        AddParam(cmd, "@cid", conversationId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
    }

    private async Task SetCheckpointAsync(string conversationId, long sequence, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO conversation_projection_checkpoints
            (conversation_id, projected_through, updated_at)
            VALUES (@cid, @seq, @now)";
        AddParam(cmd, "@cid", conversationId);
        AddParam(cmd, "@seq", sequence);
        AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
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
