namespace PuddingScheduling;

/// <summary>The persisted result of applying a tick, including firings rejected by the unique key.</summary>
public sealed record ScheduleTickWrite(bool Applied, int Created, int AlreadyRecorded);

/// <summary>Persistence boundary for the time-trigger layer.</summary>
public interface IScheduleStore
{
    /// <summary>Returns enabled schedules with a due cursor, oldest first.</summary>
    Task<IReadOnlyList<AutomationScheduleRecord>> ListDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the cursor, each new firing and each corresponding task together. A stale or disabled
    /// snapshot writes nothing. Only a conflict on the canonical firing key counts as already recorded.
    /// </summary>
    Task<ScheduleTickWrite> TryApplyTickAsync(
        AutomationScheduleRecord schedule,
        TickPlan plan,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
