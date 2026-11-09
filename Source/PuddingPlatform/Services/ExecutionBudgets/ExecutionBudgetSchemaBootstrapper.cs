using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 S5 idempotent SQLite schema bootstrap for the durable budget ledger (same style as the Goal
/// bootstrapper: CREATE TABLE IF NOT EXISTS + idempotent indexes; column names mirror the entities).
/// Existing databases are upgraded without dropping rows; decimal cost is stored as invariant TEXT.
/// </summary>
public static class ExecutionBudgetSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS execution_budget_accounts (
            account_id                TEXT    NOT NULL PRIMARY KEY,
            parent_account_id         TEXT,
            root_kind                 INTEGER NOT NULL,
            root_id                   TEXT    NOT NULL,
            workspace_id              TEXT    NOT NULL,
            agent_id                  TEXT    NOT NULL,
            session_id                TEXT    NOT NULL,
            cost_unit                 TEXT    NOT NULL,
            max_output_tokens         INTEGER,
            max_cost                  TEXT,
            max_calls                 INTEGER,
            max_concurrent_attempts   INTEGER NOT NULL DEFAULT 2,
            max_input_tokens_per_request INTEGER,
            enforcement_mode  INTEGER NOT NULL DEFAULT 0,
            input_tokens              INTEGER NOT NULL DEFAULT 0,
            output_tokens             INTEGER NOT NULL DEFAULT 0,
            cache_hit_tokens          INTEGER NOT NULL DEFAULT 0,
            cost                      TEXT    NOT NULL DEFAULT '0',
            reserved_output_tokens    INTEGER NOT NULL DEFAULT 0,
            reserved_cost             TEXT    NOT NULL DEFAULT '0',
            unknown_output_tokens     INTEGER NOT NULL DEFAULT 0,
            unknown_cost              TEXT    NOT NULL DEFAULT '0',
            calls                     INTEGER NOT NULL DEFAULT 0,
            active_attempts           INTEGER NOT NULL DEFAULT 0,
            revision                  INTEGER NOT NULL DEFAULT 0,
            cancelled                 INTEGER NOT NULL DEFAULT 0,
            cancellation_revision     INTEGER NOT NULL DEFAULT 0,
            reason_code               TEXT,
            failure_code              TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS execution_budget_reservations (
            reservation_id            TEXT    NOT NULL PRIMARY KEY,
            account_id                TEXT    NOT NULL,
            root_account_id           TEXT    NOT NULL,
            attempt_id                TEXT    NOT NULL,
            operation_id              TEXT    NOT NULL,
            fingerprint               TEXT    NOT NULL,
            chain                     TEXT    NOT NULL,
            cost_unit                 TEXT    NOT NULL,
            max_input_tokens          INTEGER NOT NULL,
            reserved_output_tokens    INTEGER NOT NULL,
            reserved_cost             TEXT    NOT NULL,
            state                     INTEGER NOT NULL,
            owner_epoch               INTEGER NOT NULL DEFAULT 0,
            receipt_input_tokens      INTEGER,
            receipt_output_tokens     INTEGER,
            receipt_cache_hit_tokens  INTEGER,
            receipt_cost              TEXT,
            receipt_cost_unit         TEXT,
            receipt_overrun           INTEGER,
            receipt_from_unknown      INTEGER,
            receipt_fingerprint       TEXT,
            receipt_revision          INTEGER
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_execution_budget_reservations_attempt ON execution_budget_reservations(attempt_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_execution_budget_reservations_operation ON execution_budget_reservations(operation_id);",
        "CREATE INDEX IF NOT EXISTS IX_execution_budget_reservations_account ON execution_budget_reservations(account_id);",
        "CREATE INDEX IF NOT EXISTS IX_execution_budget_accounts_parent ON execution_budget_accounts(parent_account_id);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db, ILogger? logger,
        CancellationToken ct = default)
    {
        foreach (var ddl in Ddl)
            await db.Database.ExecuteSqlRawAsync(ddl, ct).ConfigureAwait(false);
        logger?.LogInformation("PA-01 execution budget schema bootstrap completed ({Count} statements).", Ddl.Length);
    }
}
