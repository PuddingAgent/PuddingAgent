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
        // ── workspace_tasks（TB-02 + structured routing）─────────────
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
            task_type            TEXT    NOT NULL DEFAULT 'general',
            required_capabilities_json TEXT NOT NULL DEFAULT '[]',
            required_provider_id TEXT,
            required_model_id    TEXT,
            allow_agent_fallback INTEGER NOT NULL DEFAULT 0,
            auto_dispatch_enabled INTEGER NOT NULL DEFAULT 0,
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
            origin               INTEGER,
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

        // ── task_comments（TB-11，8 列 + long Id 自增主键）──────────
        """
        CREATE TABLE IF NOT EXISTS task_comments (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            comment_id     TEXT    NOT NULL,
            task_id        TEXT    NOT NULL,
            workspace_id   TEXT    NOT NULL,
            author_kind    INTEGER NOT NULL,
            author_id      TEXT,
            content        TEXT    NOT NULL,
            created_at_utc TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_comments_comment_id ON task_comments(comment_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_comments_task ON task_comments(task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_comments_workspace_task ON task_comments(workspace_id, task_id);",
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

        await EnsureColumnAsync(db, "workspace_tasks", "task_type", "TEXT NOT NULL DEFAULT 'general'", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "required_capabilities_json", "TEXT NOT NULL DEFAULT '[]'", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "required_provider_id", "TEXT", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "required_model_id", "TEXT", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "allow_agent_fallback", "INTEGER NOT NULL DEFAULT 0", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "auto_dispatch_enabled", "INTEGER NOT NULL DEFAULT 0", logger, ct);
        await EnsureColumnAsync(db, "workspace_tasks", "sort_order", "INTEGER NOT NULL DEFAULT 0", logger, ct);

        // IX_workspace_tasks_workspace_sort 引用 sort_order：旧库 ALTER 补列必须发生在索引创建之前，
        // 故该索引从上方 Ddl 数组移至此（全新库路径 EnsureColumnAsync 为 no-op，行为等价）。
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_workspace_tasks_workspace_sort ON workspace_tasks(workspace_id, sort_order);",
            ct);
    }

    private static async Task EnsureColumnAsync(
        PlatformDbContext db,
        string tableName,
        string columnName,
        string definition,
        ILogger? logger,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await check.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        logger?.LogInformation("[WorkspaceTaskSchema] adding column {Table}.{Column}", tableName, columnName);
        await alter.ExecuteNonQueryAsync(ct);
    }
}
