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
            workspace_task_id          TEXT,
            workspace_task_version     INTEGER,
            plan_version               INTEGER NOT NULL DEFAULT 1,
            schema_version             INTEGER NOT NULL DEFAULT 1,
            plan_kind                  TEXT    NOT NULL DEFAULT 'delegation',
            plan_fingerprint           TEXT,
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
        "ALTER TABLE task_plan_runs ADD COLUMN workspace_task_id TEXT;",
        "ALTER TABLE task_plan_runs ADD COLUMN workspace_task_version INTEGER;",
        "ALTER TABLE task_plan_runs ADD COLUMN plan_version INTEGER NOT NULL DEFAULT 1;",
        "ALTER TABLE task_plan_runs ADD COLUMN schema_version INTEGER NOT NULL DEFAULT 1;",
        "ALTER TABLE task_plan_runs ADD COLUMN plan_kind TEXT NOT NULL DEFAULT 'delegation';",
        "ALTER TABLE task_plan_runs ADD COLUMN plan_fingerprint TEXT;",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_plan_runs_workspace_task_version ON task_plan_runs(workspace_id, workspace_task_id, workspace_task_version, plan_version) WHERE workspace_task_id IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS IX_task_plan_runs_plan_fingerprint ON task_plan_runs(plan_fingerprint) WHERE plan_fingerprint IS NOT NULL;",

        """
        CREATE TABLE IF NOT EXISTS task_nodes (
            "Id"                        INTEGER PRIMARY KEY AUTOINCREMENT,
            task_node_id                TEXT    NOT NULL,
            plan_id                     TEXT    NOT NULL,
            parent_task_node_id          TEXT,
            depth                       INTEGER NOT NULL DEFAULT 0,
            sequence_no                 INTEGER NOT NULL DEFAULT 0,
            work_unit_kind              TEXT,
            depends_on_json             TEXT,
            scope_json                  TEXT,
            required_capability_ids_json TEXT,
            max_rounds                  INTEGER,
            max_tool_calls              INTEGER,
            max_duration_seconds        INTEGER,
            max_input_tokens            INTEGER,
            max_output_tokens           INTEGER,
            max_cost                    TEXT,
            retry_policy_json           TEXT,
            progress_fingerprint        TEXT,
            checkpoint_artifact_ref     TEXT,
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
        "ALTER TABLE task_nodes ADD COLUMN sequence_no INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE task_nodes ADD COLUMN work_unit_kind TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN depends_on_json TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN scope_json TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN required_capability_ids_json TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN max_rounds INTEGER;",
        "ALTER TABLE task_nodes ADD COLUMN max_tool_calls INTEGER;",
        "ALTER TABLE task_nodes ADD COLUMN max_duration_seconds INTEGER;",
        "ALTER TABLE task_nodes ADD COLUMN max_input_tokens INTEGER;",
        "ALTER TABLE task_nodes ADD COLUMN max_output_tokens INTEGER;",
        "ALTER TABLE task_nodes ADD COLUMN max_cost TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN retry_policy_json TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN progress_fingerprint TEXT;",
        "ALTER TABLE task_nodes ADD COLUMN checkpoint_artifact_ref TEXT;",
        "CREATE INDEX IF NOT EXISTS IX_task_nodes_plan_sequence ON task_nodes(plan_id, sequence_no);",

        """
        CREATE TABLE IF NOT EXISTS work_unit_await_handles (
            await_handle_id TEXT NOT NULL,
            plan_id TEXT NOT NULL,
            task_node_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            external_id TEXT,
            status TEXT NOT NULL DEFAULT 'waiting',
            fencing_token INTEGER NOT NULL DEFAULT 0,
            metadata_json TEXT,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            signaled_at_utc TEXT,
            consumed_at_utc TEXT,
            PRIMARY KEY (await_handle_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_work_unit_await_handles_plan_node_status ON work_unit_await_handles(plan_id, task_node_id, status);",
        "CREATE INDEX IF NOT EXISTS IX_work_unit_await_handles_external ON work_unit_await_handles(kind, external_id) WHERE external_id IS NOT NULL;",
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
