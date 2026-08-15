using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;

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
                await UpsertCatalogRowAsync(evt, ct);
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

                var parentSessionId = await ResolveParentSessionAsync(evt.ConversationId);

        await tokenUsageRecorder.RecordRequiredAsync(
            usage,
            sourceType: "agent_llm",
            sourceId: evt.EventId,
            workspaceId: evt.WorkspaceId,
            sessionId: evt.ConversationId,
            providerId: providerId,
            modelId: modelId,
            occurredAtUtc: evt.OccurredAt,
            parentSessionId: parentSessionId);
    }

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

    /// <summary>
    /// P0-4f 第⑤步第⑤小步 C2: 在事件循环内为每个 conversation_events 事件 UPSERT 一行 conversation_catalog。
    /// 实现优先级：(1) 匹配 STOP_CONDITION 的状态机映射；(2) 复用同一 DbContext 连接做 title 反查与 UPSERT，不引入第二事务。
    /// </summary>
    /// <remarks>
    /// <para><b>状态机</b>：turn.accepted→'active'；turn.completed→'idle'；turn.failed→'failed'；
    /// turn.cancelled→'cancelled'；context.compaction.completed→'frozen' 并写 successor_conversation_id；
    /// 其他类型→保持上次 status，仅更新 last_active_at。</para>
    /// <para><b>字段</b>：principal_id = evt.AgentId；created_at = 首事件 occurred_at（INSERT 写一次，ON CONFLICT 不覆盖）；
    /// last_active_at = 每事件 occurred_at；title 采用 pick A——仅 turn.accepted 时由 payload.userMessageId（或 evt.MessageId）
    /// 反查 db.ChatMessages 的 Content 截前 30 字；parent_conversation_id 本次一律 NULL（后续步骤处理）。</para>
    /// </remarks>
    private async Task UpsertCatalogRowAsync(ConversationEvent evt, CancellationToken ct)
    {
        // 状态机映射：mappedStatus = NULL 的事件类型在 INSERT 分支回落 SQL DEFAULT 'active',
        // 在 DO UPDATE 分支由 CASE 子句守住以保留 conversation_catalog.status 原值，天然完成"留原值"语义。
        string? mappedStatus = evt.Type switch
        {
            ConversationEventTypes.TurnAccepted => "active",
            ConversationEventTypes.TurnCompleted => "idle",
            ConversationEventTypes.TurnFailed => "failed",
            ConversationEventTypes.TurnCancelled => "cancelled",
            ConversationEventTypes.ContextCompactionCompleted => "frozen",
            _ => null,
        };

        // successor_conversation_id：仅 context.compaction.completed 事件写
        // （payload.newConversationId；字段名参见 CompactionSessionSuccessor.CreateAsync）。
        var successorId = evt.Type == ConversationEventTypes.ContextCompactionCompleted
            ? ExtractString(evt.Payload, "newConversationId")
            : null;

        // pick A — title 反查：仅 turn.accepted 事件发起，从 payload.userMessageId（或 evt.MessageId）
        // 反查 db.ChatMessages 的 Content 并截前 30 字。未命中留 NULL 由后续 backfill 补。
        // stop_condition 要求 title 在 DO UPDATE 不覆盖（保留首次值）：
        // 由 COALESCE(conversation_catalog.title, @title) 实现——existing title 非空时不动，
        // existing title 为 NULL 时用 @title 一次性 backfill（后续 turn.accepted 仍能补）。
        string? title = null;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        if (evt.Type == ConversationEventTypes.TurnAccepted)
        {
            var msgId = ExtractString(evt.Payload, "userMessageId") ?? evt.MessageId;
            if (!string.IsNullOrEmpty(msgId))
            {
                try
                {
                    var content = await db.ChatMessages
                        .AsNoTracking()
                        .Where(m => m.MessageId == msgId)
                        .Select(m => m.Content)
                        .FirstOrDefaultAsync(ct);
                    if (!string.IsNullOrEmpty(content))
                    {
                        title = content.Length > 30
                            ? content.Substring(0, 30)
                            : content;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "[ConversationProjector] catalog title lookup failed conv={Conv} msgId={Msg}",
                        evt.ConversationId, msgId);
                    // title 留 NULL，后续 backfill 流程补
                }
            }
        }

        // UPSERT — 复用 title 反查的同一 DbContext/连接，不引入第二事务。
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO conversation_catalog
              (conversation_id, workspace_id, agent_id, principal_id, title, status,
               created_at, last_active_at, parent_conversation_id, successor_conversation_id)
            VALUES (@cid, @wsid, @aid, @pid, @title, COALESCE(@status, 'active'),
                    @ca, @la, NULL, @succ)
            ON CONFLICT(conversation_id) DO UPDATE SET
              last_active_at = @la,
              status = CASE WHEN @status IS NULL
                            THEN conversation_catalog.status
                            ELSE @status END,
              title = COALESCE(conversation_catalog.title, @title),
              agent_id = COALESCE(@aid, conversation_catalog.agent_id),
              principal_id = COALESCE(@pid, conversation_catalog.principal_id),
              successor_conversation_id = COALESCE(@succ, conversation_catalog.successor_conversation_id)";

        AddParam(cmd, "@cid", evt.ConversationId);
        AddParam(cmd, "@wsid", evt.WorkspaceId);
        AddParam(cmd, "@aid", (object?)evt.AgentId ?? DBNull.Value);
        AddParam(cmd, "@pid", (object?)evt.AgentId ?? DBNull.Value);
        AddParam(cmd, "@title", (object?)title ?? DBNull.Value);
        AddParam(cmd, "@status", (object?)mappedStatus ?? DBNull.Value);
        AddParam(cmd, "@ca", evt.OccurredAt.ToString("O"));
        AddParam(cmd, "@la", evt.OccurredAt.ToString("O"));
        AddParam(cmd, "@succ", (object?)successorId ?? DBNull.Value);
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
