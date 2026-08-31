using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// P0 Scheduler 事件驱动层：task_scheduler_intents 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="PuddingPlatform.Services.Tasks.TaskDispatchSchemaBootstrapper"/> 同风格：
/// EF EnsureCreated 覆盖全新库，本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS +
/// 幂等索引）。列名与 <see cref="Data.Entities.TaskSchedulerIntentEntity"/> 的 [Column] 严格一致。
/// </para>
/// </summary>
public static class TaskSchedulerIntentSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_scheduler_intents (
            intent_id        TEXT    NOT NULL PRIMARY KEY,
            workspace_id     TEXT    NOT NULL,
            source           TEXT    NOT NULL,
            source_event_id  INTEGER NOT NULL,
            event_type       TEXT    NOT NULL,
            task_id          TEXT,
            goal_run_id      TEXT,
            payload_json     TEXT,
            status           TEXT    NOT NULL DEFAULT 'pending',
            attempt_count    INTEGER NOT NULL DEFAULT 0,
            lease_owner      TEXT,
            lease_until_utc  TEXT,
            created_at_utc   TEXT    NOT NULL,
            processed_at_utc TEXT,
            last_error       TEXT
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_scheduler_intents_source_event ON task_scheduler_intents(source, source_event_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intents_status_created ON task_scheduler_intents(status, created_at_utc);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intents_task ON task_scheduler_intents(task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intents_workspace_status ON task_scheduler_intents(workspace_id, status);",
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
                    "[TaskSchedulerIntentSchema] bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
