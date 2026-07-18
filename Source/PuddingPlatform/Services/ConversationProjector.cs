using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
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
    ILogger<ConversationProjector> logger)
{
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
