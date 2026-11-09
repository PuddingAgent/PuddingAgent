using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Eventing;

/// <summary>Idempotent SQLite schema bootstrap for plugin archive/audit records.</summary>
public static class PluginArchiveSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS plugin_archive_records (
            archive_id   TEXT NOT NULL PRIMARY KEY,
            operation_id TEXT NOT NULL,
            event_id     TEXT NOT NULL,
            workspace_id TEXT NOT NULL,
            plugin_id    TEXT NOT NULL,
            artifact_ref TEXT NOT NULL,
            payload_hash TEXT NOT NULL,
            recorded_at  TEXT NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ix_plugin_archive_records_operation_id ON plugin_archive_records(operation_id);",
        "CREATE INDEX IF NOT EXISTS ix_plugin_archive_records_workspace_event ON plugin_archive_records(workspace_id, event_id);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db, Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken ct = default)
    {
        foreach (var statement in Ddl)
        {
            logger?.LogDebug("Ensuring plugin archive schema: {Statement}", statement);
            await db.Database.ExecuteSqlRawAsync(statement, ct).ConfigureAwait(false);
        }
    }
}
