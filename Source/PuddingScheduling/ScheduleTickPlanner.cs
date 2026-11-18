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

/// <summary>
/// One tick's decision: what to fire, where to advance, and the count of dropped occurrences covered
/// by the bounded scan. When <see cref="SkippedIsLowerBound"/> is true, the scan did not count the whole backlog.
/// </summary>
public sealed record TickPlan(
    IReadOnlyList<DateTimeOffset> FireTimes,
    DateTimeOffset? NextRunAtUtc,
    int Skipped,
    bool SkippedIsLowerBound = false)
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

        // Inspect one extra due occurrence so overflow is visible without walking a long outage.
        var dueTimes = new List<DateTimeOffset>();
        DateTimeOffset? cursor = due;
        while (cursor is { } occurrence && occurrence <= nowUtc && dueTimes.Count <= maxCatchUp)
        {
            dueTimes.Add(occurrence);
            cursor = cron.NextOccurrence(timeZone, occurrence);
        }

        var truncated = cursor <= nowUtc;
        var nextRunAt = truncated ? cron.NextOccurrence(timeZone, nowUtc) : cursor;

        switch (state.CatchUp)
        {
            case CatchUpPolicy.Skip:
                // Skip missed minutes, including after a long outage, while still firing the live minute.
                var currentMinute = new DateTimeOffset(ScheduleFireKey.NormalizeFireTime(nowUtc));
                var countedCurrent = dueTimes.Contains(currentMinute);
                var live = countedCurrent || (truncated && cron.IsOccurrence(timeZone, currentMinute));
                return new TickPlan(
                    live ? [currentMinute] : [],
                    nextRunAt,
                    dueTimes.Count - (countedCurrent ? 1 : 0),
                    truncated);

            case CatchUpPolicy.RunOnce:
                // Keep the *original* fire time as the key: repeated restarts must not produce
                // a second run for the same missed window.
                return new TickPlan([dueTimes[0]], nextRunAt, dueTimes.Count - 1, truncated);

            case CatchUpPolicy.RunAll:
                var kept = dueTimes.Take(maxCatchUp).ToArray();
                return new TickPlan(kept, nextRunAt, dueTimes.Count - kept.Length, truncated);

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state.CatchUp, "Unhandled catch-up policy.");
        }
    }
}
