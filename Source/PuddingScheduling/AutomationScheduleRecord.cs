namespace PuddingScheduling;

/// <summary>
/// The persisted shape of one automation schedule, as defined by the design proposal
/// (§3.2 of Docs/11_design/定时自动化任务-设计-2026-11-18.md).
///
/// This type holds no persistence behaviour on purpose: the store contract and the EF mapping
/// belong to the host, so this project stays a leaf that can be tested without a database.
/// </summary>
public sealed record AutomationScheduleRecord(
    Guid Id,
    string Name,
    string CronExpression,
    string TimeZoneId,
    string Handler,
    string? PayloadJson,
    CatchUpPolicy CatchUp,
    bool IsEnabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset? LastFireTimeUtc)
{
    /// <summary>
    /// Projects this record onto the minimal state <see cref="ScheduleTickPlanner"/> needs.
    ///
    /// Kept as an explicit method rather than object-initialiser gymnastics so a future field
    /// reorder cannot silently feed the planner the wrong values: the mapping is a position-by-name
    /// assertion that a test can lock down.
    /// </summary>
    public ScheduleState ToState() => new(
        IsEnabled: IsEnabled,
        NextRunAtUtc: NextRunAtUtc,
        LastFireTimeUtc: LastFireTimeUtc,
        CatchUp: CatchUp);
}

/// <summary>
/// The idempotency key for one schedule firing: <c>(scheduleId, scheduled fire time)</c>.
///
/// The design (§3.2) makes a unique index on these two columns the *only* guarantee that a
/// schedule does not fire twice for the same instant. That guarantee is worthless unless both
/// sides compute an identical key, so the normalisation below is the part that matters:
///
/// * The fire time is converted to UTC. A schedule stored in Asia/Shanghai and the same instant
///   expressed with a +08:00 offset must produce one key, not two — otherwise a restart that
///   changes how the offset is materialised would double-fire.
/// * The fire time is truncated to whole minutes. The scheduler's precision is minutes (§4 of the
///   design), so seconds and ticks are noise: two ticks that observe the same occurrence must not
///   disagree because one of them carried a leftover sub-minute component.
/// </summary>
public readonly record struct ScheduleFireKey(Guid ScheduleId, DateTime FireTimeUtc)
{
    /// <summary>Builds the key for one firing, normalising the fire time as described on the type.</summary>
    public static ScheduleFireKey Create(Guid scheduleId, DateTimeOffset fireTime) =>
        new(scheduleId, NormalizeFireTime(fireTime));

    /// <summary>
    /// Reduces a fire time to its canonical UTC minute. Exposed separately because the store
    /// implementation needs the same value for the column it writes, and duplicating the rule in
    /// two places is exactly how the two sides drift apart.
    /// </summary>
    public static DateTime NormalizeFireTime(DateTimeOffset fireTime)
    {
        var utc = fireTime.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
    }
}
