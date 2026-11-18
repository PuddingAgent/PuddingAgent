namespace PuddingScheduling;

/// <summary>
/// The DDL for the two automation tables, kept in the component rather than in the persistence
/// adapter or in a test fixture.
///
/// <para>
/// The reason is the same one that drove <see cref="ScheduleFireKey"/>: the unique index is the
/// only thing stopping a schedule from firing twice, so its definition must exist in exactly one
/// place. If the test declared its own CREATE TABLE, the test would keep passing while the shipped
/// schema dropped the constraint — the classic "the test proves the fixture, not the product" trap.
/// </para>
///
/// <para>
/// Timestamps are stored as ISO-8601 UTC text, matching how the rest of this codebase persists
/// <c>DateTimeOffset</c> in SQLite.
/// </para>
/// </summary>
public static class ScheduleStoreSchema
{
    /// <summary>Table holding schedule definitions.</summary>
    public const string SchedulesTable = "automation_schedules";

    /// <summary>Table holding one row per attempted firing; the unique index lives here.</summary>
    public const string RunsTable = "automation_runs";

    /// <summary>Name of the constraint that makes a firing unique. Asserted by name so a rename is a test failure.</summary>
    public const string FireUniquenessIndexName = "ux_automation_runs_fire";

    /// <summary>Creates the schedules table.</summary>
    public const string CreateSchedulesSql = """
        CREATE TABLE IF NOT EXISTS automation_schedules (
            id                 TEXT    NOT NULL PRIMARY KEY,
            name               TEXT    NOT NULL,
            cron_expression    TEXT    NOT NULL,
            time_zone_id       TEXT    NOT NULL,
            handler            TEXT    NOT NULL,
            payload_json       TEXT    NULL,
            catch_up_policy    INTEGER NOT NULL,
            is_enabled         INTEGER NOT NULL,
            next_run_at_utc    TEXT    NULL,
            last_run_at_utc    TEXT    NULL,
            last_fire_time_utc TEXT    NULL,
            revision           INTEGER NOT NULL DEFAULT 0
        );
        """;

    /// <summary>
    /// Creates the runs table together with the uniqueness constraint. The constraint is inline in
    /// the CREATE TABLE rather than a separate CREATE UNIQUE INDEX so that a schema created by any
    /// code path necessarily carries it.
    /// </summary>
    public const string CreateRunsSql = """
        CREATE TABLE IF NOT EXISTS automation_runs (
            id                      TEXT    NOT NULL PRIMARY KEY,
            schedule_id             TEXT    NOT NULL,
            scheduled_fire_time_utc TEXT    NOT NULL,
            task_id                 TEXT    NULL,
            started_at_utc          TEXT    NOT NULL,
            status                  INTEGER NOT NULL,
            error                   TEXT    NULL,
            CONSTRAINT ux_automation_runs_fire UNIQUE (schedule_id, scheduled_fire_time_utc)
        );
        """;

    private const string CreateDueIndexSql = """
        CREATE INDEX IF NOT EXISTS ix_automation_schedules_due ON automation_schedules (next_run_at_utc, id)
        WHERE is_enabled = 1 AND next_run_at_utc IS NOT NULL;
        """;

    /// <summary>Every statement needed to bring an empty database to the current schema.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { CreateSchedulesSql, CreateRunsSql, CreateDueIndexSql };
}
