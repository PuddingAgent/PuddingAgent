namespace PuddingScheduling;

/// <summary>What to do with fire times that were missed while the host was down.</summary>
public enum CatchUpPolicy
{
    /// <summary>Catch nothing up; only advance the cursor. Right for "inspect the house now" work.</summary>
    Skip,

    /// <summary>Fire exactly once regardless of how many were missed. The original fire time is kept as the idempotency key.</summary>
    RunOnce,

    /// <summary>Fire every missed occurrence, bounded by the planner's cap.</summary>
    RunAll,
}

/// <summary>The persisted part of a schedule that the planner needs. Kept as a record so a plan is a pure function of it.</summary>
public sealed record ScheduleState(
    bool IsEnabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastFireTimeUtc,
    CatchUpPolicy CatchUp);

/// <summary>One tick's decision: what to fire now, how far to advance, and how much was dropped on purpose.</summary>
public sealed record TickPlan(
    IReadOnlyList<DateTimeOffset> FireTimes,
    DateTimeOffset? NextRunAtUtc,
    int Skipped)
{
    public static readonly TickPlan None = new(Array.Empty<DateTimeOffset>(), null, 0);
}

/// <summary>
/// Decides what a single scheduler tick should do, with no database, no clock and no logging.
///
/// Separating this from the hosted service is deliberate: the difficult part of a scheduler is
/// not the timer, it is the missed-occurrence policy, and that policy is worth testing without
/// standing up a host. The caller persists the returned plan and uses
/// (scheduleId, fireTime) as the idempotency key.
/// </summary>
public static class ScheduleTickPlanner
{
    /// <summary>Upper bound on how many occurrences a single <see cref="CatchUpPolicy.RunAll"/> tick may emit.</summary>
    public const int DefaultMaxCatchUp = 10;

    public static TickPlan Plan(
        CronExpression cron,
        TimeZoneInfo timeZone,
        ScheduleState state,
        DateTimeOffset nowUtc,
        int maxCatchUp = DefaultMaxCatchUp)
    {
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(state);

        if (maxCatchUp < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCatchUp), maxCatchUp, "Must be at least 1.");

        if (!state.IsEnabled)
            return TickPlan.None;

        // First tick ever: seed the cursor, fire nothing. A newly created schedule must not
        // immediately "catch up" on a year of history.
        if (state.NextRunAtUtc is null)
            return new TickPlan(Array.Empty<DateTimeOffset>(), cron.NextOccurrence(timeZone, nowUtc), 0);

        var due = state.NextRunAtUtc.Value;
        if (due > nowUtc)
            return new TickPlan(Array.Empty<DateTimeOffset>(), due, 0);

        // Collect every occurrence that is due, up to one more than the cap so the overflow
        // is observable rather than silently truncated.
        var dueTimes = new List<DateTimeOffset>();
        var cursor = due;
        while (cursor <= nowUtc && dueTimes.Count <= maxCatchUp)
        {
            dueTimes.Add(cursor);
            var next = cron.NextOccurrence(timeZone, cursor);
            if (next is null) break;
            cursor = next.Value;
        }

        // cursor is now the first occurrence strictly after now (or the last one if the cron
        // cannot produce another). If the loop stopped on the collection cap, advance once more.
        var nextRunAt = cursor > nowUtc ? cursor : cron.NextOccurrence(timeZone, cursor);

        switch (state.CatchUp)
        {
            case CatchUpPolicy.Skip:
                return new TickPlan(Array.Empty<DateTimeOffset>(), nextRunAt, dueTimes.Count);

            case CatchUpPolicy.RunOnce:
                // Keep the *original* fire time as the key: repeated restarts must not produce
                // a second run for the same missed window.
                return new TickPlan(new[] { dueTimes[0] }, nextRunAt, dueTimes.Count - 1);

            case CatchUpPolicy.RunAll:
                if (dueTimes.Count > maxCatchUp)
                {
                    var kept = dueTimes.Take(maxCatchUp).ToArray();
                    return new TickPlan(kept, nextRunAt, dueTimes.Count - maxCatchUp);
                }
                return new TickPlan(dueTimes, nextRunAt, 0);

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state.CatchUp, "Unhandled catch-up policy.");
        }
    }
}
