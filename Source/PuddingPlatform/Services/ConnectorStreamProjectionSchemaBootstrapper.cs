using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Adds the connector streaming projection table to existing SQLite development databases.
/// Clean databases are created by EF; the bootstrap is intentionally idempotent.
/// </summary>
public static class ConnectorStreamProjectionSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS connector_stream_projections (
            "Id"                     INTEGER PRIMARY KEY AUTOINCREMENT,
            projection_id             TEXT    NOT NULL,
            command_id                TEXT    NOT NULL,
            workspace_id              TEXT    NOT NULL,
            conversation_id           TEXT    NOT NULL,
            message_id                TEXT    NOT NULL,
            connector_id              TEXT    NOT NULL,
            external_conversation_id  TEXT    NOT NULL,
            external_message_id       TEXT    NOT NULL,
            external_resource_id      TEXT,
            external_reply_id         TEXT,
            element_id                TEXT    NOT NULL,
            status                    TEXT    NOT NULL,
            operation_sequence        INTEGER NOT NULL DEFAULT 0,
            last_event_sequence       INTEGER NOT NULL DEFAULT 0,
            pending_event_sequence    INTEGER,
            content                   TEXT    NOT NULL DEFAULT '',
            attempt_count             INTEGER NOT NULL DEFAULT 0,
            available_at              INTEGER,
            last_error                TEXT,
            created_at                INTEGER NOT NULL,
            updated_at                INTEGER NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_connector_stream_projection_id ON connector_stream_projections(projection_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_connector_stream_command_connector ON connector_stream_projections(command_id, connector_id);",
        "CREATE INDEX IF NOT EXISTS idx_connector_stream_status_available ON connector_stream_projections(status, available_at, updated_at);",
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        foreach (var ddl in Ddl)
            await db.Database.ExecuteSqlRawAsync(ddl, ct);

        logger?.LogInformation("[ConnectorStreamSchema] Projection table ensured");
    }
}
