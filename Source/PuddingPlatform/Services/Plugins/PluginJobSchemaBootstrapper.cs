using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Plugins;

/// <summary>PA-07A S5 idempotent SQLite schema bootstrap for the durable plugin job protocol.</summary>
public static class PluginJobSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS plugin_jobs (
            job_id              TEXT    NOT NULL PRIMARY KEY,
            plugin_id           TEXT    NOT NULL,
            plugin_generation   INTEGER NOT NULL,
            workspace_id        TEXT    NOT NULL,
            owner_work_id       TEXT    NOT NULL,
            root_budget_id      TEXT    NOT NULL,
            kind                TEXT    NOT NULL,
            operation_id        TEXT    NOT NULL,
            input_ref           TEXT    NOT NULL,
            input_hash          TEXT    NOT NULL,
            timeout_seconds     INTEGER NOT NULL,
            required_grants     TEXT    NOT NULL,
            deadline            TEXT    NOT NULL,
            accepted_at         TEXT    NOT NULL,
            accepted_sequence   INTEGER NOT NULL,
            state               INTEGER NOT NULL,
            reason_code         TEXT,
            result_ref          TEXT,
            result_hash         TEXT,
            updated_at          TEXT    NOT NULL,
            attempt             INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS plugin_job_operations (
            operation_key       TEXT    NOT NULL PRIMARY KEY,
            job_id              TEXT    NOT NULL,
            fingerprint         TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_plugin_jobs_operation ON plugin_jobs(workspace_id, operation_id);",
        "CREATE INDEX IF NOT EXISTS IX_plugin_jobs_workspace ON plugin_jobs(workspace_id, accepted_sequence);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db, ILogger? logger, CancellationToken ct = default)
    {
        foreach (var ddl in Ddl)
            await db.Database.ExecuteSqlRawAsync(ddl, ct).ConfigureAwait(false);
        logger?.LogInformation("PA-07A plugin job schema bootstrap completed ({Count} statements).", Ddl.Length);
    }
}
