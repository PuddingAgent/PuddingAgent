using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Orchestration;

/// <summary>Idempotent SQLite schema bootstrap for durable agent orchestration.</summary>
public static class AgentOrchestrationSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS orchestration_graphs (
            graph_id             TEXT    PRIMARY KEY,
            workspace_id         TEXT    NOT NULL,
            root_session_id      TEXT    NOT NULL,
            created_by_agent_id  TEXT    NOT NULL,
            objective            TEXT    NOT NULL,
            current_revision     INTEGER NOT NULL,
            current_revision_id  TEXT    NOT NULL,
            created_at           INTEGER NOT NULL,
            updated_at           INTEGER NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_orchestration_graphs_workspace_updated ON orchestration_graphs(workspace_id, updated_at DESC);",

        """
        CREATE TABLE IF NOT EXISTS orchestration_graph_revisions (
            revision_id          TEXT    PRIMARY KEY,
            graph_id             TEXT    NOT NULL,
            revision             INTEGER NOT NULL,
            parent_revision_id   TEXT,
            schema_version       TEXT    NOT NULL,
            definition_json      TEXT    NOT NULL,
            content_hash         TEXT    NOT NULL,
            created_by_agent_id  TEXT    NOT NULL,
            created_at           INTEGER NOT NULL,
            FOREIGN KEY(graph_id) REFERENCES orchestration_graphs(graph_id) ON DELETE CASCADE
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_orchestration_revisions_graph_revision ON orchestration_graph_revisions(graph_id, revision);",
        "CREATE INDEX IF NOT EXISTS IX_orchestration_revisions_graph_created ON orchestration_graph_revisions(graph_id, created_at DESC);",

        """
        CREATE TABLE IF NOT EXISTS orchestration_graph_layouts (
            graph_id             TEXT    NOT NULL,
            base_revision_id     TEXT    NOT NULL,
            layout_revision      INTEGER NOT NULL,
            layout_json          TEXT    NOT NULL,
            updated_at           INTEGER NOT NULL,
            PRIMARY KEY(graph_id, base_revision_id),
            FOREIGN KEY(graph_id) REFERENCES orchestration_graphs(graph_id) ON DELETE CASCADE,
            FOREIGN KEY(base_revision_id) REFERENCES orchestration_graph_revisions(revision_id) ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_orchestration_layouts_graph_updated ON orchestration_graph_layouts(graph_id, updated_at DESC);",

        """
        CREATE TABLE IF NOT EXISTS orchestration_runs (
            run_id                 TEXT    PRIMARY KEY,
            graph_id               TEXT    NOT NULL,
            revision_id            TEXT    NOT NULL,
            workspace_id           TEXT    NOT NULL,
            root_session_id        TEXT    NOT NULL,
            requested_by_agent_id  TEXT    NOT NULL,
            status                 TEXT    NOT NULL,
            version                INTEGER NOT NULL,
            head_sequence          INTEGER NOT NULL,
            max_concurrency        INTEGER NOT NULL,
            created_at             INTEGER NOT NULL,
            activated_at           INTEGER,
            updated_at             INTEGER NOT NULL,
            completed_at           INTEGER,
            error_message          TEXT,
            FOREIGN KEY(graph_id) REFERENCES orchestration_graphs(graph_id),
            FOREIGN KEY(revision_id) REFERENCES orchestration_graph_revisions(revision_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_orchestration_runs_workspace_status_updated ON orchestration_runs(workspace_id, status, updated_at DESC);",
        "CREATE INDEX IF NOT EXISTS IX_orchestration_runs_graph_created ON orchestration_runs(graph_id, created_at DESC);",

        """
        CREATE TABLE IF NOT EXISTS orchestration_run_inputs (
            run_id              TEXT NOT NULL,
            input_id            TEXT NOT NULL,
            value_json          TEXT NOT NULL,
            PRIMARY KEY(run_id, input_id),
            FOREIGN KEY(run_id) REFERENCES orchestration_runs(run_id) ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_orchestration_run_inputs_run ON orchestration_run_inputs(run_id, input_id);",

        """
        CREATE TABLE IF NOT EXISTS orchestration_node_runs (
            run_id              TEXT    NOT NULL,
            node_id             TEXT    NOT NULL,
            node_kind           TEXT    NOT NULL,
            status              TEXT    NOT NULL,
            attempt             INTEGER NOT NULL,
            max_attempts        INTEGER NOT NULL,
            claim_id            TEXT,
            lease_owner         TEXT,
            lease_until         INTEGER,
            fencing_token       INTEGER NOT NULL,
            execution_run_id    TEXT,
            sub_session_id      TEXT,
            output_summary      TEXT,
            artifact_reference  TEXT,
            outputs_json        TEXT    NOT NULL DEFAULT '{{}}',
            error_message       TEXT,
            started_at          INTEGER,
            completed_at        INTEGER,
            updated_at          INTEGER NOT NULL,
            PRIMARY KEY(run_id, node_id),
            FOREIGN KEY(run_id) REFERENCES orchestration_runs(run_id) ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_orchestration_node_runs_ready ON orchestration_node_runs(run_id, status, node_id);",
        "CREATE INDEX IF NOT EXISTS IX_orchestration_node_runs_lease ON orchestration_node_runs(run_id, status, lease_until);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_orchestration_node_runs_claim ON orchestration_node_runs(claim_id) WHERE claim_id IS NOT NULL;",

        """
        CREATE TABLE IF NOT EXISTS orchestration_run_events (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id            TEXT    NOT NULL,
            run_id              TEXT    NOT NULL,
            graph_id            TEXT    NOT NULL,
            revision_id         TEXT    NOT NULL,
            sequence            INTEGER NOT NULL,
            event_type          TEXT    NOT NULL,
            node_id             TEXT,
            execution_run_id    TEXT,
            sub_session_id      TEXT,
            summary             TEXT,
            artifact_reference  TEXT,
            attributes_json     TEXT    NOT NULL,
            recorded_at         INTEGER NOT NULL,
            FOREIGN KEY(run_id) REFERENCES orchestration_runs(run_id) ON DELETE CASCADE
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_orchestration_events_event_id ON orchestration_run_events(event_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_orchestration_events_run_sequence ON orchestration_run_events(run_id, sequence);",
        "CREATE INDEX IF NOT EXISTS IX_orchestration_events_run_recorded ON orchestration_run_events(run_id, recorded_at);"
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
                    "[AgentOrchestrationSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }

        // CREATE TABLE IF NOT EXISTS does not evolve an existing development database. Keep the
        // bootstrap idempotent while adding the first durable, port-addressable node output fact.
        await EnsureColumnAsync(
            db,
            "orchestration_node_runs",
            "outputs_json",
            "ALTER TABLE orchestration_node_runs ADD COLUMN outputs_json TEXT NOT NULL DEFAULT '{{}}'",
            ct);
    }

    private static async Task EnsureColumnAsync(
        PlatformDbContext db,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            await reader.DisposeAsync();
            await db.Database.ExecuteSqlRawAsync(alterSql, ct);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }
}
