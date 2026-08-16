using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-05: task_dispatch_outbox + task_execution_bindings 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="MessageFabric.MessageFabricSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖
/// 全新库，本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与
/// <see cref="Data.Entities.TaskDispatchOutboxEntity"/> / <see cref="Data.Entities.TaskExecutionBindingEntity"/>
/// 的 [Column] 严格一致。
/// </para>
/// </summary>
public static class TaskDispatchSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_dispatch_outbox (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key   TEXT    NOT NULL,
            workspace_id      TEXT    NOT NULL,
            task_id           TEXT    NOT NULL,
            assignment_id     TEXT    NOT NULL,
            agent_id          TEXT    NOT NULL,
            origin            TEXT    NOT NULL,
            envelope_payload  TEXT    NOT NULL,
            status            TEXT    NOT NULL DEFAULT 'pending',
            attempt_count     INTEGER NOT NULL DEFAULT 0,
            last_error        TEXT,
            lease_until_utc   TEXT,
            created_at_utc    TEXT    NOT NULL,
            sent_at_utc       TEXT
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_dispatch_outbox_idempotency ON task_dispatch_outbox(idempotency_key);",
        "CREATE INDEX IF NOT EXISTS IX_task_dispatch_outbox_status_lease ON task_dispatch_outbox(status, lease_until_utc);",
        "CREATE INDEX IF NOT EXISTS IX_task_dispatch_outbox_assignment ON task_dispatch_outbox(assignment_id);",

        """
        CREATE TABLE IF NOT EXISTS task_execution_bindings (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id        TEXT    NOT NULL,
            assignment_id  TEXT    NOT NULL,
            delivery_id    TEXT    NOT NULL,
            execution_id   TEXT,
            session_id     TEXT,
            bound_at_utc   TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_execution_bindings_task_assignment_delivery ON task_execution_bindings(task_id, assignment_id, delivery_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_execution_bindings_delivery ON task_execution_bindings(delivery_id);",
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
                    "[TaskDispatchSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
