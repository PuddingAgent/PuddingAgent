namespace PuddingScheduling;

/// <summary>
/// What happened when the scheduler tried to record a firing.
///
/// <see cref="Created"/> and <see cref="AlreadyRecorded"/> are the two outcomes the idempotency
/// guarantee is allowed to produce. "Already recorded" is not an error: two ticks racing over the
/// same occurrence is normal, and the loser must exit quietly.
/// </summary>
public enum ScheduleFireOutcome
{
    /// <summary>This is the first time this (schedule, fire time) pair was recorded.</summary>
    Created,

    /// <summary>Another writer already recorded this exact occurrence; nothing was written.</summary>
    AlreadyRecorded,
}

/// <summary>
/// The persistence contract for automation schedules. Implemented by the host (EF Core / SQLite);
/// declared here so the scheduling component stays a leaf that can be tested without a database.
///
/// <para>
/// <b>The one rule that matters:</b> <see cref="TryRecordFireAsync"/> must derive
/// <see cref="ScheduleFireOutcome.AlreadyRecorded"/> from a unique-constraint violation, never from
/// a "select then insert" check. A pre-check is a time-of-check/time-of-use race: two concurrent
/// ticks both see zero rows, both insert, and the schedule fires twice. The design (Docs/11_design/
/// 定时自动化任务-设计-2026-11-18.md §3.2) therefore makes the unique index on
/// <c>(schedule_id, scheduled_fire_time_utc)</c> the single point of truth, and this contract
/// forbids implementations from inventing a weaker one.
/// </para>
/// </summary>
public interface IScheduleStore
{
    /// <summary>
    /// Returns enabled schedules whose <c>NextRunAtUtc</c> is at or before <paramref name="nowUtc"/>,
    /// oldest first. Ordered deliberately: a starved schedule must not be pushed further back by a
    /// fresh one every tick.
    /// </summary>
    Task<IReadOnlyList<AutomationScheduleRecord>> ListDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one firing, atomically. Returns <see cref="ScheduleFireOutcome.Created"/> when the
    /// row was inserted, or <see cref="ScheduleFireOutcome.AlreadyRecorded"/> when the unique
    /// constraint rejected it. Implementations must catch the constraint violation and translate it;
    /// they must not swallow other database errors.
    /// </summary>
    Task<ScheduleFireOutcome> TryRecordFireAsync(
        Guid runId,
        Guid scheduleId,
        DateTimeOffset fireTime,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the schedule's cursor forward after a tick. Separate from
    /// <see cref="TryRecordFireAsync"/> on purpose: recording a firing and advancing the cursor are
    /// two facts with different failure modes, and a crash between them must leave the cursor
    /// behind (causing a harmless re-evaluation) rather than ahead (causing a silent skip).
    /// </summary>
    Task AdvanceCursorAsync(
        Guid scheduleId,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset lastFireTimeUtc,
        CancellationToken cancellationToken);
}
