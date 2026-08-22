using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.ExternalApi;

/// <summary>
/// ADR-075: task_evaluations + external_api_idempotency 的幂等 SQLite schema bootstrap。
/// </summary>
public static class ExternalTaskApiSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── task_evaluations（追加式评价）────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS task_evaluations (
            evaluation_id           TEXT    NOT NULL,
            task_id                 TEXT    NOT NULL,
            workspace_id            TEXT    NOT NULL,
            verdict                 TEXT    NOT NULL,
            score                   INTEGER NOT NULL,
            comment                 TEXT    NOT NULL,
            task_version_observed   INTEGER NOT NULL,
            supersedes_evaluation_id TEXT,
            evaluator_type          TEXT    NOT NULL,
            evaluator_id            TEXT    NOT NULL,
            evaluator_display_name  TEXT    NOT NULL,
            created_at_utc          TEXT    NOT NULL,
            PRIMARY KEY (evaluation_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_task_evaluations_task ON task_evaluations(task_id, created_at_utc);",
        "CREATE INDEX IF NOT EXISTS IX_task_evaluations_workspace ON task_evaluations(workspace_id);",

        // ── external_api_idempotency（mutation 幂等事实）─────────────
        """
        CREATE TABLE IF NOT EXISTS external_api_idempotency (
            idempotency_key_hash TEXT    NOT NULL,
            token_id             TEXT    NOT NULL,
            request_hash         TEXT    NOT NULL,
            response_status      INTEGER NOT NULL,
            resource_id          TEXT,
            created_at_utc       TEXT    NOT NULL,
            PRIMARY KEY (idempotency_key_hash)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_external_api_idempotency_created ON external_api_idempotency(created_at_utc);",
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        foreach (var ddl in Ddl)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "[ExternalTaskApiSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
