using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Idempotent schema bootstrap for the conservative Agent availability,
/// logical work reservation, and WorkspaceTask dependency facts.
/// </summary>
public static class TaskSchedulingSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS agent_availability_projection (
            id                       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            workspace_id             TEXT    NOT NULL,
            agent_id                 TEXT    NOT NULL,
            state                    INTEGER NOT NULL,
            activity_reason          INTEGER NOT NULL,
            version                  INTEGER NOT NULL,
            observed_at_utc          TEXT    NOT NULL,
            valid_until_utc          TEXT    NOT NULL,
            idle_since_utc           TEXT,
            main_conversation_id     TEXT,
            active_turn_id           TEXT,
            active_execution_id      TEXT,
            active_task_id           TEXT,
            active_goal_run_id       TEXT,
            active_sub_agent_run_id  TEXT,
            reservation_id           TEXT,
            cooldown_until_utc       TEXT,
            reason_code              TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_agent_availability_workspace_agent ON agent_availability_projection(workspace_id, agent_id);",
        "CREATE INDEX IF NOT EXISTS IX_agent_availability_state_valid ON agent_availability_projection(workspace_id, state, valid_until_utc);",

        """
        CREATE TABLE IF NOT EXISTS agent_execution_reservations (
            fencing_token   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            reservation_id  TEXT    NOT NULL,
            workspace_id    TEXT    NOT NULL,
            agent_id        TEXT    NOT NULL,
            task_id         TEXT    NOT NULL,
            goal_run_id     TEXT,
            owner_id        TEXT    NOT NULL,
            status          TEXT    NOT NULL DEFAULT 'active',
            lease_until_utc TEXT    NOT NULL,
            created_at_utc  TEXT    NOT NULL,
            updated_at_utc  TEXT    NOT NULL,
            released_at_utc TEXT,
            release_reason  TEXT
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_agent_reservations_id ON agent_execution_reservations(reservation_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_agent_reservations_active_agent ON agent_execution_reservations(workspace_id, agent_id) WHERE status = 'active';",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_agent_reservations_active_task ON agent_execution_reservations(workspace_id, task_id) WHERE status = 'active';",
        "CREATE INDEX IF NOT EXISTS IX_agent_reservations_status_lease ON agent_execution_reservations(status, lease_until_utc);",

        """
        CREATE TABLE IF NOT EXISTS task_dependencies (
            dependency_id       TEXT NOT NULL PRIMARY KEY,
            workspace_id        TEXT NOT NULL,
            predecessor_task_id TEXT NOT NULL,
            successor_task_id   TEXT NOT NULL,
            kind                TEXT NOT NULL DEFAULT 'finish_to_start',
            created_at_utc      TEXT NOT NULL,
            CHECK (predecessor_task_id <> successor_task_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_dependencies_edge ON task_dependencies(workspace_id, predecessor_task_id, successor_task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_dependencies_successor ON task_dependencies(workspace_id, successor_task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_dependencies_predecessor ON task_dependencies(workspace_id, predecessor_task_id);",
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
                logger?.LogWarning(
                    ex,
                    "[TaskSchedulingSchema] bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
