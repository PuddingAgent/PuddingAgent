using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// P0-4f 第⑤步第⑤小步 C2/C3：conversation_catalog 物化投影的唯一权威 UPSERT 实现。
/// <para>
/// C2 实时投影（<see cref="ConversationProjector"/>）与 C3 历史回填
/// （<see cref="ConversationCatalogBackfillService"/>）共用本类，避免状态机映射与
/// UPSERT SQL 出现第二份漂移逻辑。
/// </para>
/// </summary>
public sealed class ConversationCatalogWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<ConversationCatalogWriter> logger)
{
    /// <summary>
    /// 为单个 conversation_events 事件 UPSERT 一行 conversation_catalog。
    /// 语义与 C2 实时投影完全一致（本方法即从 ConversationProjector 原方法提取，逻辑未变）。
    /// </summary>
    /// <remarks>
    /// <para><b>状态机</b>：turn.accepted→'active'；turn.completed→'idle'；turn.failed→'failed'；
    /// turn.cancelled→'cancelled'；context.compaction.completed→'frozen' 并写 successor_conversation_id；
    /// 其他类型→保持上次 status，仅更新 last_active_at。</para>
    /// <para><b>字段</b>：principal_id = evt.AgentId；created_at = 首事件 occurred_at（INSERT 写一次，ON CONFLICT 不覆盖）；
    /// last_active_at = 每事件 occurred_at；title 采用 pick A——仅 turn.accepted 时由 payload.userMessageId（或 evt.MessageId）
    /// 反查 db.ChatMessages 的 Content 截前 30 字；parent_conversation_id 本次一律 NULL（后续步骤处理）。</para>
    /// </remarks>
    public async Task UpsertCatalogRowAsync(ConversationEvent evt, CancellationToken ct)
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
                        "[ConversationCatalogWriter] catalog title lookup failed conv={Conv} msgId={Msg}",
                        evt.ConversationId, msgId);
                    // title 留 NULL，后续 backfill 流程补
                }
            }
        }

        // UPSERT — 复用 title 反查的同一 DbContext/连接，不引入第二事务。
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
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
}
