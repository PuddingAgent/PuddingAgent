using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Reviews;

/// <summary>PA-03 S5 idempotent SQLite schema bootstrap for durable review jobs (Goal/Task bootstrapper style).</summary>
public static class ReviewJobSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS review_jobs (
            job_id                        TEXT    NOT NULL PRIMARY KEY,
            review_id                     TEXT    NOT NULL,
            workspace_id                  TEXT    NOT NULL,
            parent_work_id                TEXT,
            goal_run_id                   TEXT,
            activation_epoch              INTEGER NOT NULL,
            iteration_no                  INTEGER NOT NULL,
            objective_version             TEXT    NOT NULL,
            acceptance_contract_version   TEXT    NOT NULL,
            artifact_set_hash             TEXT    NOT NULL,
            review_policy_version         TEXT    NOT NULL,
            required                      INTEGER NOT NULL,
            budget_account_id             TEXT    NOT NULL,
            actor_id                      TEXT,
            deadline                      TEXT    NOT NULL,
            accepted_at                   TEXT    NOT NULL,
            accepted_sequence             INTEGER NOT NULL,
            state                         INTEGER NOT NULL,
            revision                      INTEGER NOT NULL,
            owner_epoch                   INTEGER NOT NULL DEFAULT 0,
            lease_until                   INTEGER,
            outcome_finding               INTEGER,
            outcome_level                 INTEGER,
            outcome_criteria_json         TEXT,
            reason_code                   TEXT,
            updated_at                    TEXT    NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS review_job_operations (
            operation_id                  TEXT    NOT NULL PRIMARY KEY,
            job_id                        TEXT    NOT NULL,
            review_id                     TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_review_jobs_review ON review_jobs(review_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_review_jobs_logical_key ON review_jobs(workspace_id, parent_work_id, goal_run_id, activation_epoch, iteration_no, objective_version, acceptance_contract_version, artifact_set_hash, review_policy_version);",
        "CREATE INDEX IF NOT EXISTS IX_review_job_operations_job ON review_job_operations(job_id);",
        "CREATE INDEX IF NOT EXISTS IX_review_jobs_state ON review_jobs(state, lease_until);",
        """
        CREATE TABLE IF NOT EXISTS review_events (
            event_id                      TEXT    NOT NULL PRIMARY KEY,
            review_id                     TEXT    NOT NULL,
            job_id                        TEXT    NOT NULL,
            workspace_id                  TEXT    NOT NULL,
            parent_work_id                TEXT,
            goal_run_id                   TEXT,
            activation_epoch              INTEGER NOT NULL DEFAULT 0,
            iteration_no                  INTEGER NOT NULL DEFAULT 0,
            objective_version             TEXT    NOT NULL DEFAULT '',
            acceptance_contract_version   TEXT    NOT NULL DEFAULT '',
            artifact_set_hash             TEXT    NOT NULL DEFAULT '',
            review_policy_version         TEXT    NOT NULL DEFAULT '',
            finding                       INTEGER NOT NULL,
            level                         INTEGER NOT NULL,
            revision                      INTEGER NOT NULL,
            occurred_at                   TEXT    NOT NULL,
            occurred_at_ms                INTEGER NOT NULL DEFAULT 0,
            published                     INTEGER NOT NULL DEFAULT 0,
            published_at                  TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_review_events_unpublished ON review_events(published, occurred_at);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db, ILogger? logger,
        CancellationToken ct = default)
    {
        foreach (var ddl in Ddl)
            await db.Database.ExecuteSqlRawAsync(ddl, ct).ConfigureAwait(false);
        logger?.LogInformation("PA-03 review job schema bootstrap completed ({Count} statements).", Ddl.Length);
    }
}
