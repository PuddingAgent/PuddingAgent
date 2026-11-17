using PuddingScheduling;

namespace PuddingSchedulingTests;

[TestClass]
public sealed class ScheduleTickPlannerTests
{
    private static readonly TimeZoneInfo Cst =
        TimeZoneInfo.CreateCustomTimeZone("Test/CST", TimeSpan.FromHours(8), "CST", "CST");

    /// <summary>Daily at 08:00 local.</summary>
    private static readonly CronExpression Daily8 = CronExpression.Parse("0 8 * * *");

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void Disabled_ProducesNoPlan()
    {
        var state = new ScheduleState(false, At(2026, 11, 18, 8, 0), null, CatchUpPolicy.RunAll);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0));

        Assert.IsEmpty(plan.FireTimes, "a disabled schedule must not fire");
        Assert.IsNull(plan.NextRunAtUtc, "a disabled schedule must not move its cursor");
    }

    [TestMethod]
    public void FirstTick_SeedsCursorWithoutFiring()
    {
        var state = new ScheduleState(true, null, null, CatchUpPolicy.RunAll);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0));

        Assert.IsEmpty(plan.FireTimes, "a brand new schedule must not fire immediately");
        Assert.AreEqual(At(2026, 11, 19, 8, 0), plan.NextRunAtUtc);
    }

    [TestMethod]
    public void NotYetDue_ProducesNoPlan_AndKeepsCursor()
    {
        var due = At(2026, 11, 19, 8, 0);
        var state = new ScheduleState(true, due, At(2026, 11, 18, 8, 0), CatchUpPolicy.RunAll);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 20, 0));

        Assert.IsEmpty(plan.FireTimes, "nothing is due yet");
        Assert.AreEqual(due, plan.NextRunAtUtc, "the cursor must stay where it was");
    }

    [TestMethod]
    public void DueExactly_WhenNowEqualsCursor_Fires()
    {
        var due = At(2026, 11, 19, 8, 0);
        var state = new ScheduleState(true, due, null, CatchUpPolicy.RunOnce);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, due);

        CollectionAssert.AreEqual(new[] { due }, plan.FireTimes.ToArray());
    }

    [TestMethod]
    public void SkipPolicy_MissedDays_FiresNothing_ButAdvancesPastNow()
    {
        // Host was down from 11-15 to 11-18 12:00; four 08:00 slots are in the past.
        var state = new ScheduleState(true, At(2026, 11, 15, 8, 0), null, CatchUpPolicy.Skip);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0));

        Assert.IsEmpty(plan.FireTimes, "Skip must not fire anything");
        Assert.AreEqual(4, plan.Skipped, "15,16,17,18 = four missed slots");
        Assert.AreEqual(At(2026, 11, 19, 8, 0), plan.NextRunAtUtc);
    }

    [TestMethod]
    public void RunOncePolicy_MissedDays_FiresExactlyOnce_WithOriginalFireTime()
    {
        var first = At(2026, 11, 15, 8, 0);
        var state = new ScheduleState(true, first, null, CatchUpPolicy.RunOnce);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0));

        Assert.HasCount(1, plan.FireTimes);
        Assert.AreEqual(first, plan.FireTimes[0], "the idempotency key must stay the original fire time");
        Assert.AreEqual(3, plan.Skipped);
        Assert.AreEqual(At(2026, 11, 19, 8, 0), plan.NextRunAtUtc);
    }

    [TestMethod]
    public void RunAllPolicy_MissedDays_FiresEverySlot()
    {
        var state = new ScheduleState(true, At(2026, 11, 15, 8, 0), null, CatchUpPolicy.RunAll);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0));

        CollectionAssert.AreEqual(
            new[] { At(2026, 11, 15, 8, 0), At(2026, 11, 16, 8, 0), At(2026, 11, 17, 8, 0), At(2026, 11, 18, 8, 0) },
            plan.FireTimes.ToArray());
        Assert.AreEqual(0, plan.Skipped);
    }

    [TestMethod]
    public void RunAllPolicy_IsCapped_AndReportsWhatItDropped()
    {
        var state = new ScheduleState(true, At(2026, 11, 1, 8, 0), null, CatchUpPolicy.RunAll);
        var plan = ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0), maxCatchUp: 3);

        Assert.HasCount(3, plan.FireTimes, "the cap must bound a single tick");
        Assert.IsGreaterThan(0, plan.Skipped, "the overflow must be reported, not silently dropped");
    }

    [TestMethod]
    public void Plan_IsDeterministic_ForIdenticalInput()
    {
        var state = new ScheduleState(true, At(2026, 11, 15, 8, 0), null, CatchUpPolicy.RunAll);
        var now = At(2026, 11, 18, 12, 0);

        var a = ScheduleTickPlanner.Plan(Daily8, Cst, state, now);
        var b = ScheduleTickPlanner.Plan(Daily8, Cst, state, now);

        CollectionAssert.AreEqual(a.FireTimes.ToArray(), b.FireTimes.ToArray());
        Assert.AreEqual(a.NextRunAtUtc, b.NextRunAtUtc);
        Assert.AreEqual(a.Skipped, b.Skipped);
    }

    [TestMethod]
    public void MaxCatchUpBelowOne_IsRejected()
    {
        var state = new ScheduleState(true, At(2026, 11, 15, 8, 0), null, CatchUpPolicy.RunAll);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ScheduleTickPlanner.Plan(Daily8, Cst, state, At(2026, 11, 18, 12, 0), maxCatchUp: 0));
    }
}
