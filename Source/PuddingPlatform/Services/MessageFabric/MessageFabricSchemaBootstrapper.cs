using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>
/// Idempotent SQLite schema bootstrap for ADR-045 message fabric tables.
/// <para>
/// EF migrations cover clean databases, but existing local SQLite databases may
/// predate message fabric migrations. This service keeps startup compatible by
/// creating only the missing message-domain tables and indexes.
/// </para>
/// </summary>
public static class MessageFabricSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS room_messages (
            "Id"                INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id          TEXT    NOT NULL,
            workspace_id        TEXT    NOT NULL DEFAULT 'default',
            room_id             TEXT    NOT NULL,
            from_kind           TEXT    NOT NULL,
            from_id             TEXT    NOT NULL,
            from_display_name   TEXT,
            audience            TEXT    NOT NULL,
            visibility          TEXT    NOT NULL,
            content             TEXT    NOT NULL,
            conversation_id     TEXT,
            reply_to_message_id TEXT,
            correlation_id      TEXT,
            causation_id        TEXT,
            metadata_json       TEXT,
            created_at          INTEGER NOT NULL
        );
        """,
        "ALTER TABLE room_messages ADD COLUMN conversation_id TEXT;",
        "ALTER TABLE room_messages ADD COLUMN reply_to_message_id TEXT;",
        "ALTER TABLE room_messages ADD COLUMN correlation_id TEXT;",
        "ALTER TABLE room_messages ADD COLUMN causation_id TEXT;",
        "ALTER TABLE room_messages ADD COLUMN metadata_json TEXT;",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_room_messages_message_id ON room_messages(message_id);",
        "CREATE INDEX IF NOT EXISTS idx_room_messages_workspace_room_time ON room_messages(workspace_id, room_id, created_at);",

        """
        CREATE TABLE IF NOT EXISTS message_deliveries (
            "Id"                  INTEGER PRIMARY KEY AUTOINCREMENT,
            delivery_id           TEXT    NOT NULL,
            message_id            TEXT    NOT NULL,
            workspace_id          TEXT    NOT NULL DEFAULT 'default',
            room_id               TEXT,
            target_kind           TEXT    NOT NULL,
            target_id             TEXT    NOT NULL,
            target_display_name   TEXT,
            status                TEXT    NOT NULL DEFAULT 'queued',
            priority              INTEGER NOT NULL DEFAULT 0,
            attempt_count         INTEGER NOT NULL DEFAULT 0,
            available_at          INTEGER,
            lease_until           INTEGER,
            claimed_by_execution_id TEXT,
            last_error            TEXT,
            created_at            INTEGER NOT NULL,
            updated_at            INTEGER NOT NULL,
            read_at               INTEGER,
            ack_at                INTEGER
        );
        """,
        "ALTER TABLE message_deliveries ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE message_deliveries ADD COLUMN available_at INTEGER;",
        "ALTER TABLE message_deliveries ADD COLUMN lease_until INTEGER;",
        "ALTER TABLE message_deliveries ADD COLUMN claimed_by_execution_id TEXT;",
        "ALTER TABLE message_deliveries ADD COLUMN defer_count INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE message_deliveries ADD COLUMN execution_state TEXT;",
        "ALTER TABLE message_deliveries ADD COLUMN last_error TEXT;",
        // Phase 2 projection contract: one-time dirty-data normalization (idempotent).
        // attempt_count > 3 truncation: historical attempt counts up to 248 were
        // inflated by the old busy spin-loop bug; the real retry cap is 3.
        "UPDATE message_deliveries SET attempt_count = 3 WHERE attempt_count > 3;",
        // retrying + attempt_count >= 3 + availableAt already expired -> dead_letter.
        "UPDATE message_deliveries SET status = 'dead_letter' WHERE status = 'retrying' AND attempt_count >= 3 AND available_at IS NOT NULL AND available_at <= (strftime('%s','now') * 1000);",
        // defer_count defaults to 0 for existing rows (no historical data, conservative).
        // execution_state is parsed from last_error: contains "busy" (case-insensitive) -> 'Busy'.
        "UPDATE message_deliveries SET execution_state = CASE WHEN instr(lower(coalesce(last_error,'')), 'busy') > 0 THEN 'Busy' ELSE NULL END;",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_message_deliveries_delivery_id ON message_deliveries(delivery_id);",
        "CREATE INDEX IF NOT EXISTS idx_message_deliveries_message_id ON message_deliveries(message_id);",
        "CREATE INDEX IF NOT EXISTS idx_message_deliveries_endpoint_status ON message_deliveries(workspace_id, target_kind, target_id, status);",
        "CREATE INDEX IF NOT EXISTS idx_message_deliveries_claim ON message_deliveries(workspace_id, target_kind, target_id, status, available_at, priority, created_at);",
        "CREATE INDEX IF NOT EXISTS idx_message_deliveries_room_time ON message_deliveries(workspace_id, room_id, created_at);",
        "CREATE INDEX IF NOT EXISTS idx_message_deliveries_lease_until ON message_deliveries(lease_until);",

        """
        CREATE TABLE IF NOT EXISTS room_participants (
            "Id"              INTEGER PRIMARY KEY AUTOINCREMENT,
            participant_id    TEXT    NOT NULL,
            workspace_id      TEXT    NOT NULL DEFAULT 'default',
            room_id           TEXT    NOT NULL,
            kind              TEXT    NOT NULL,
            endpoint_id       TEXT    NOT NULL,
            display_name      TEXT,
            avatar_url        TEXT,
            can_send          INTEGER NOT NULL DEFAULT 1,
            can_receive       INTEGER NOT NULL DEFAULT 1,
            status            TEXT    NOT NULL DEFAULT 'available',
            created_at        INTEGER NOT NULL,
            updated_at        INTEGER NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_room_participants_participant_id ON room_participants(participant_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_room_participants_endpoint ON room_participants(workspace_id, room_id, kind, endpoint_id);",
    ];

    /// <summary>
    /// Chat UI P0#1/P1#5: conversation 事件类型白名单（审批卡片 + Plan 模式事件）。
    /// <para>
    /// Message Fabric / SSE 管道对事件类型是透传的（type 为不透明字符串），
    /// 这里显式注册审批与计划事件类型作为唯一入口，供 schema bootstrap、诊断与
    /// 未来校验共用，避免魔术字符串散落。
    /// </para>
    /// </summary>
    public static readonly string[] ConversationEventTypeAllowlist =
        [.. ApprovalEventTypes.All, .. PlanEventTypes.All];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        foreach (var ddl in Ddl)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex)
            {
                if (ddl.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                logger?.LogWarning(
                    ex,
                    "[MessageFabricSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
