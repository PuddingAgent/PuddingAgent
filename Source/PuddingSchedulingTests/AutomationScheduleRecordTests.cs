namespace PuddingSchedulingTests;

using PuddingScheduling;

/// <summary>
/// Locks down the two things that decide whether a schedule can fire twice:
/// the record-to-planner projection, and the (scheduleId, fireTime) idempotency key.
///
/// Both are trivial-looking code that fails silently. A swapped projection still compiles and
/// still schedules something; an un-normalised key still compiles and still writes rows — it just
/// writes two of them for one occurrence. These tests exist because the compiler cannot see either.
/// </summary>
[TestClass]
public sealed class AutomationScheduleRecordTests
{
    private static AutomationScheduleRecord Sample(
        CatchUpPolicy catchUp = CatchUpPolicy.RunOnce,
        bool isEnabled = true) => new(
            Id: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Name: "nightly inspect",
            CronExpression: "0 8 * * *",
            TimeZoneId: "Asia/Shanghai",
            Handler: "inspect_house",
            PayloadJson: null,
            CatchUp: catchUp,
            IsEnabled: isEnabled,
            NextRunAtUtc: new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero),
            LastRunAtUtc: new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero),
            LastFireTimeUtc: new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public void ToState_MapsEachFieldByName_NotByPosition()
    {
        var record = Sample(catchUp: CatchUpPolicy.RunAll, isEnabled: true);

        var state = record.ToState();

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual(record.NextRunAtUtc, state.NextRunAtUtc);
        Assert.AreEqual(record.LastFireTimeUtc, state.LastFireTimeUtc);
        Assert.AreEqual(CatchUpPolicy.RunAll, state.CatchUp);
    }

    [TestMethod]
    public void ToState_DoesNotConfuseNextRunWithLastFire()
    {
        // Two distinct values so a swapped mapping cannot pass by coincidence.
        var record = Sample() with
        {
            NextRunAtUtc = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            LastFireTimeUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };

        var state = record.ToState();

        Assert.AreEqual(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), state.NextRunAtUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), state.LastFireTimeUtc);
    }

    [TestMethod]
    public void ToState_CarriesDisabledFlag()
    {
        Assert.IsFalse(Sample(isEnabled: false).ToState().IsEnabled);
    }

    [TestMethod]
    public void FireKey_SameInstantInDifferentOffsets_IsOneKey()
    {
        var id = Guid.NewGuid();
        var utc = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var shanghai = utc.ToOffset(TimeSpan.FromHours(8));

        var a = ScheduleFireKey.Create(id, utc);
        var b = ScheduleFireKey.Create(id, shanghai);

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.FireTimeUtc, b.FireTimeUtc);
    }

    [TestMethod]
    public void FireKey_TruncatesSubMinuteComponents()
    {
        var id = Guid.NewGuid();
        var clean = new DateTimeOffset(2026, 11, 18, 0, 5, 0, TimeSpan.Zero);
        var noisy = new DateTimeOffset(2026, 11, 18, 0, 5, 37, 250, TimeSpan.Zero);

        Assert.AreEqual(ScheduleFireKey.Create(id, clean), ScheduleFireKey.Create(id, noisy));
    }

    [TestMethod]
    public void FireKey_DifferentMinutes_AreDifferentKeys()
    {
        var id = Guid.NewGuid();
        var a = ScheduleFireKey.Create(id, new DateTimeOffset(2026, 11, 18, 0, 5, 0, TimeSpan.Zero));
        var b = ScheduleFireKey.Create(id, new DateTimeOffset(2026, 11, 18, 0, 6, 0, TimeSpan.Zero));

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void FireKey_DifferentSchedules_SameInstant_AreDifferentKeys()
    {
        var instant = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        var a = ScheduleFireKey.Create(Guid.NewGuid(), instant);
        var b = ScheduleFireKey.Create(Guid.NewGuid(), instant);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void NormalizeFireTime_ReturnsUtcKind()
    {
        var local = new DateTimeOffset(2026, 11, 18, 8, 30, 0, TimeSpan.FromHours(8));

        var normalized = ScheduleFireKey.NormalizeFireTime(local);

        // SQLite stores no kind; if we ever hand it a Local/Unspecified value the round trip
        // starts shifting by the machine's offset. Pin it to Utc.
        Assert.AreEqual(DateTimeKind.Utc, normalized.Kind);
        Assert.AreEqual(new DateTime(2026, 11, 18, 0, 30, 0, DateTimeKind.Utc), normalized);
    }
}
