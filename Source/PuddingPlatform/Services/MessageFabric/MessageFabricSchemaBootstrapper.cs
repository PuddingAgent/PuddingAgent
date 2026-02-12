using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

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
            created_at          INTEGER NOT NULL
        );
        """,
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
