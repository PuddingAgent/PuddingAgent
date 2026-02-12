using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.TaskPlanning;

/// <summary>
/// Idempotent SQLite schema bootstrap for task planning tables.
/// <para>
/// EF migrations cover clean databases. Existing local SQLite databases may predate
/// task-planning schema creation, so startup should call this to create missing
/// tables/columns/indexes safely.
/// </para>
/// </summary>
public static class TaskPlanningSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_plan_runs (
            "Id"                INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id                    TEXT    NOT NULL,
            workspace_id               TEXT    NOT NULL,
            root_session_id            TEXT    NOT NULL,
            leader_agent_id            TEXT    NOT NULL,
            objective                  TEXT,
            status                     TEXT    NOT NULL DEFAULT 'Draft',
            max_delegation_depth       INTEGER NOT NULL DEFAULT 2,
            default_allow_sub_delegation INTEGER NOT NULL DEFAULT 1,
            allow_agent_creation_by_leader INTEGER NOT NULL DEFAULT 1,
            max_active_task_nodes_per_plan INTEGER NOT NULL DEFAULT 50,
            created_at                 INTEGER NOT NULL,
            updated_at                 INTEGER NOT NULL,
            completed_at               INTEGER,
            result_summary             TEXT,
            error_message              TEXT,
            trace_id                   TEXT,
            correlation_id             TEXT
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_task_plan_runs_plan_id ON task_plan_runs(plan_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_plan_runs_workspace_id_status_updated_at ON task_plan_runs(workspace_id, status, updated_at);",

        """
        CREATE TABLE IF NOT EXISTS task_nodes (
            "Id"                        INTEGER PRIMARY KEY AUTOINCREMENT,
            task_node_id                TEXT    NOT NULL,
            plan_id                     TEXT    NOT NULL,
            parent_task_node_id          TEXT,
            depth                       INTEGER NOT NULL DEFAULT 0,
            title                       TEXT,
            objective                   TEXT,
            input_context_summary        TEXT,
            expected_output_contract     TEXT,
            assigned_to_kind            TEXT    NOT NULL DEFAULT 'Unassigned',
            assigned_to_id               TEXT,
            assigned_template_id         TEXT,
            created_by_agent_id          TEXT,
            status                      TEXT    NOT NULL DEFAULT 'Draft',
            allow_sub_delegation        INTEGER NOT NULL DEFAULT 1,
            allow_agent_creation        INTEGER NOT NULL DEFAULT 1,
            result_summary              TEXT,
            result_artifact_ref         TEXT,
            error_message               TEXT,
            superseded_by_task_node_id   TEXT,
            started_at                  INTEGER,
            completed_at                INTEGER,
            created_at                  INTEGER NOT NULL,
            updated_at                  INTEGER NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_task_nodes_task_node_id ON task_nodes(task_node_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_nodes_plan_id_parent_task_node_id_status ON task_nodes(plan_id, parent_task_node_id, status);",
        "CREATE INDEX IF NOT EXISTS IX_task_nodes_plan_id_depth_status ON task_nodes(plan_id, depth, status);",
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
                if (ddl.StartsWith("ALTER TABLE", System.StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("duplicate column name", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                logger?.LogWarning(
                    ex,
                    "[TaskPlanningSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
