using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-02/TB-03: workspace_tasks + task_events + task_assignment_attempts 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="TaskDispatchSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与
/// <see cref="Data.Entities.WorkspaceTaskEntity"/> / <see cref="Data.Entities.TaskEventEntity"/> /
/// <see cref="Data.Entities.TaskAssignmentAttemptEntity"/> 的 [Column] 严格一致。
/// 枚举存 int、时间存 DateTimeOffset（TEXT）、列名 snake_case，与 TB-02 契约一致。
/// </para>
/// </summary>
public static class WorkspaceTaskSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── workspace_tasks（TB-02，28 列）────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS workspace_tasks (
            task_id              TEXT    NOT NULL,
            workspace_id         TEXT    NOT NULL,
            title                TEXT    NOT NULL,
            description          TEXT,
            acceptance_criteria  TEXT,
            status               INTEGER NOT NULL,
            priority             INTEGER NOT NULL,
            execution_window     INTEGER NOT NULL,
            preferred_agent_id   TEXT,
            active_assignment_id TEXT,
            not_before_utc       TEXT,
            due_at_utc           TEXT,
            next_eligible_at_utc TEXT,
            sort_order           INTEGER NOT NULL,
            progress_percent     INTEGER,
            progress_summary     TEXT,
            blocker_kind         TEXT,
            blocker_reason       TEXT,
            failure_code         TEXT,
            failure_reason       TEXT,
            version              INTEGER NOT NULL DEFAULT 1,
            created_by           TEXT,
            updated_by           TEXT,
            created_at_utc       TEXT    NOT NULL,
            updated_at_utc       TEXT    NOT NULL,
            completed_at_utc     TEXT,
            failed_at_utc        TEXT,
            archived_at_utc      TEXT,
            PRIMARY KEY (task_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_workspace_tasks_workspace_task ON workspace_tasks(workspace_id, task_id);",
        "CREATE INDEX IF NOT EXISTS IX_workspace_tasks_workspace_status ON workspace_tasks(workspace_id, status);",
        "CREATE INDEX IF NOT EXISTS IX_workspace_tasks_workspace_sort ON workspace_tasks(workspace_id, sort_order);",

        // ── task_events（TB-02，18 业务列 + long Id 自增主键）─────────
        """
        CREATE TABLE IF NOT EXISTS task_events (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id             TEXT    NOT NULL,
            task_id              TEXT    NOT NULL,
            workspace_id         TEXT    NOT NULL,
            sequence             INTEGER NOT NULL,
            event_type           INTEGER NOT NULL,
            assignment_id        TEXT,
            agent_id             TEXT,
            delivery_id          TEXT,
            execution_id         TEXT,
            session_id           TEXT,
            origin               INTEGER,
            priority             INTEGER,
            decision_code        TEXT,
            next_eligible_at_utc TEXT,
            trace_id             TEXT,
            correlation_id       TEXT,
            causation_id         TEXT,
            created_at_utc       TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_events_task_sequence ON task_events(task_id, sequence);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_events_event_id ON task_events(event_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_events_workspace_task ON task_events(workspace_id, task_id);",

        // ── task_assignment_attempts（TB-03，11 列）───────────────────
        """
        CREATE TABLE IF NOT EXISTS task_assignment_attempts (
            attempt_id      TEXT    NOT NULL,
            task_id         TEXT    NOT NULL,
            workspace_id    TEXT    NOT NULL,
            agent_id        TEXT    NOT NULL,
            attempt_number  INTEGER NOT NULL DEFAULT 1,
            status          INTEGER NOT NULL,
            window_decision TEXT,
            created_at_utc  TEXT    NOT NULL,
            updated_at_utc  TEXT    NOT NULL,
            active_at_utc   TEXT,
            released_at_utc TEXT,
            PRIMARY KEY (attempt_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_task_assignment_attempts_task ON task_assignment_attempts(task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_assignment_attempts_workspace ON task_assignment_attempts(workspace_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_assignment_attempts_task_active ON task_assignment_attempts(task_id) WHERE released_at_utc IS NULL;",
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
                    "[WorkspaceTaskSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
