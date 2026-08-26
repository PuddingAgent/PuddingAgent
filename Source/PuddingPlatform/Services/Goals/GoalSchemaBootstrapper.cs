using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-074 §12: Goal 持久控制面 5 表的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="Tasks.WorkspaceTaskSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与
/// <see cref="Data.Entities.GoalRunEntity"/> 等 5 个实体的 [Column] 严格一致。
/// 枚举存 int、时间存 DateTimeOffset（TEXT）、列名 snake_case。
/// </para>
/// <para>
/// goal_iterations / goal_outbox / goal_verifications / task_goal_bindings 在 G1 仅冻结 schema，
/// 由 G2/G3/Task-bound Goal 批次开始写入。
/// </para>
/// </summary>
public static class GoalSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── goal_runs（ADR-074 §12.1）───────────────────────────
        """
        CREATE TABLE IF NOT EXISTS goal_runs (
            goal_run_id               TEXT    NOT NULL,
            workspace_id              TEXT    NOT NULL,
            current_conversation_id   TEXT    NOT NULL,
            agent_instance_id         TEXT    NOT NULL,
            objective                 TEXT    NOT NULL,
            objective_version         INTEGER NOT NULL DEFAULT 1,
            status                    INTEGER NOT NULL DEFAULT 1,
            blocked_code              TEXT,
            blocked_message           TEXT,
            status_reason             TEXT,
            max_iterations            INTEGER NOT NULL DEFAULT 256,
            iterations_started        INTEGER NOT NULL DEFAULT 0,
            iterations_settled        INTEGER NOT NULL DEFAULT 0,
            activation_epoch          INTEGER NOT NULL DEFAULT 1,
            activation_boot_id        TEXT,
            aggregate_version         INTEGER NOT NULL DEFAULT 1,
            created_by_user_id        TEXT,
            source_channel            TEXT,
            source_command_id         TEXT,
            permission_snapshot_hash  TEXT,
            policy_snapshot_hash      TEXT,
            route_snapshot_json       TEXT,
            active_elapsed_ms         INTEGER NOT NULL DEFAULT 0,
            total_tool_calls          INTEGER NOT NULL DEFAULT 0,
            input_tokens              INTEGER NOT NULL DEFAULT 0,
            output_tokens             INTEGER NOT NULL DEFAULT 0,
            cost                      TEXT    NOT NULL DEFAULT '0',
            consecutive_no_progress   INTEGER NOT NULL DEFAULT 0,
            consecutive_same_blocker  INTEGER NOT NULL DEFAULT 0,
            consecutive_infra_failures INTEGER NOT NULL DEFAULT 0,
            last_progress_fingerprint TEXT,
            last_verification_id      TEXT,
            last_next_action          TEXT,
            cleared_at_utc            TEXT,
            created_at_utc            TEXT    NOT NULL,
            updated_at_utc            TEXT    NOT NULL,
            terminal_at_utc           TEXT,
            PRIMARY KEY (goal_run_id)
        );
        """,
        // 同一 (conversation, agent) 最多一个非终态 Goal：Active=1 / Paused=2 / Blocked=3。
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_goal_runs_active ON goal_runs(current_conversation_id, agent_instance_id) WHERE status IN (1, 2, 3);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_goal_runs_source_command ON goal_runs(source_command_id) WHERE source_command_id IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS IX_goal_runs_workspace_updated ON goal_runs(workspace_id, updated_at_utc);",

        // ── goal_iterations（ADR-074 §12.2，G2 写入）────────────
        """
        CREATE TABLE IF NOT EXISTS goal_iterations (
            goal_iteration_id    TEXT    NOT NULL,
            goal_run_id         TEXT    NOT NULL,
            activation_epoch    INTEGER NOT NULL,
            iteration_no        INTEGER NOT NULL,
            status              TEXT    NOT NULL DEFAULT 'accepted',
            command_id          TEXT,
            turn_id             TEXT,
            run_id              TEXT,
            trace_id            TEXT,
            accepted_sequence   INTEGER,
            terminal_sequence   INTEGER,
            stop_reason         TEXT,
            error_id            TEXT,
            started_at_utc      TEXT,
            settled_at_utc      TEXT,
            llm_rounds          INTEGER NOT NULL DEFAULT 0,
            tool_calls          INTEGER NOT NULL DEFAULT 0,
            input_tokens        INTEGER NOT NULL DEFAULT 0,
            output_tokens       INTEGER NOT NULL DEFAULT 0,
            progress_fingerprint TEXT,
            created_at_utc      TEXT    NOT NULL,
            PRIMARY KEY (goal_iteration_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_goal_iterations_epoch_no ON goal_iterations(goal_run_id, activation_epoch, iteration_no);",
        "CREATE INDEX IF NOT EXISTS IX_goal_iterations_goal_status ON goal_iterations(goal_run_id, status);",

        // ── goal_outbox（ADR-074 §12.4，G2 写入）────────────────
        """
        CREATE TABLE IF NOT EXISTS goal_outbox (
            outbox_id          TEXT    NOT NULL,
            goal_run_id        TEXT    NOT NULL,
            activation_epoch   INTEGER NOT NULL,
            aggregate_version  INTEGER NOT NULL,
            kind               TEXT    NOT NULL DEFAULT 'continuation',
            idempotency_key    TEXT    NOT NULL,
            payload_json       TEXT,
            status             TEXT    NOT NULL DEFAULT 'pending',
            due_at_utc         TEXT    NOT NULL,
            lease_owner        TEXT,
            lease_until_utc    TEXT,
            fencing_token      INTEGER NOT NULL DEFAULT 0,
            attempt_count      INTEGER NOT NULL DEFAULT 0,
            last_error         TEXT,
            created_at_utc     TEXT    NOT NULL,
            completed_at_utc   TEXT,
            PRIMARY KEY (outbox_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_goal_outbox_idempotency ON goal_outbox(idempotency_key);",
        "CREATE INDEX IF NOT EXISTS IX_goal_outbox_status_due ON goal_outbox(status, due_at_utc);",
        "CREATE INDEX IF NOT EXISTS IX_goal_outbox_goal ON goal_outbox(goal_run_id);",

        // ── goal_verifications（ADR-074 §12.3，G3 写入）─────────
        """
        CREATE TABLE IF NOT EXISTS goal_verifications (
            verification_id          TEXT    NOT NULL,
            goal_run_id             TEXT    NOT NULL,
            activation_epoch        INTEGER NOT NULL,
            iteration_no            INTEGER NOT NULL DEFAULT 0,
            source_turn_id          TEXT,
            source_terminal_sequence INTEGER,
            contract_version        INTEGER NOT NULL DEFAULT 1,
            route_snapshot_json     TEXT,
            status                  TEXT    NOT NULL DEFAULT 'pending',
            verdict                 TEXT,
            summary                 TEXT,
            unmet_criteria_json     TEXT,
            next_action             TEXT,
            blocker_code            TEXT,
            blocker_message         TEXT,
            evidence_refs_json      TEXT,
            raw_output_artifact_ref TEXT,
            raw_output_sha256       TEXT,
            input_tokens            INTEGER NOT NULL DEFAULT 0,
            output_tokens           INTEGER NOT NULL DEFAULT 0,
            cost                    TEXT    NOT NULL DEFAULT '0',
            error_id                TEXT,
            created_at_utc          TEXT    NOT NULL,
            completed_at_utc        TEXT,
            PRIMARY KEY (verification_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_goal_verifications_dedupe ON goal_verifications(goal_run_id, activation_epoch, source_turn_id, contract_version);",
        "CREATE INDEX IF NOT EXISTS IX_goal_verifications_goal_iteration ON goal_verifications(goal_run_id, iteration_no);",

        // ── task_goal_bindings（ADR-074 §22，Task-bound 批次写入）─
        """
        CREATE TABLE IF NOT EXISTS task_goal_bindings (
            binding_id                   TEXT    NOT NULL,
            workspace_id                 TEXT    NOT NULL,
            task_id                      TEXT    NOT NULL,
            assignment_id                TEXT,
            expected_task_version        INTEGER,
            goal_run_id                  TEXT    NOT NULL,
            agent_instance_id            TEXT    NOT NULL,
            reservation_id               TEXT,
            reservation_fencing_token    INTEGER,
            execution_window_snapshot_json TEXT,
            status                       TEXT    NOT NULL DEFAULT 'active',
            idempotency_key              TEXT,
            created_at_utc               TEXT    NOT NULL,
            released_at_utc              TEXT,
            PRIMARY KEY (binding_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_goal_bindings_goal ON task_goal_bindings(goal_run_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_goal_bindings_task_active ON task_goal_bindings(task_id) WHERE status = 'active';",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_goal_bindings_idempotency ON task_goal_bindings(idempotency_key) WHERE idempotency_key IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS IX_task_goal_bindings_workspace_status ON task_goal_bindings(workspace_id, status);",
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
                    "[GoalSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
