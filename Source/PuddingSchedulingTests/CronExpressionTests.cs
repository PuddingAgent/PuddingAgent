using PuddingScheduling;

namespace PuddingSchedulingTests;

[TestClass]
public sealed class CronExpressionTests
{
    /// <summary>Fixed +08:00 zone with no DST, so every expectation below is unambiguous.</summary>
    private static readonly TimeZoneInfo Cst =
        TimeZoneInfo.CreateCustomTimeZone("Test/CST", TimeSpan.FromHours(8), "CST", "CST");

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void Anchor_IsWednesday()
    {
        // Guards the weekday assumptions used by the weekly test below.
        Assert.AreEqual(DayOfWeek.Wednesday, At(2026, 11, 18, 12, 0).DayOfWeek);
    }

    [TestMethod]
    public void EveryMinute_AdvancesExactlyOneMinute()
    {
        var next = CronExpression.Parse("* * * * *").NextOccurrence(Cst, At(2026, 11, 18, 10, 7));
        Assert.AreEqual(At(2026, 11, 18, 10, 8), next);
    }

    [TestMethod]
    public void DailyMidnight_RollsToNextDay()
    {
        var next = CronExpression.Parse("0 0 * * *").NextOccurrence(Cst, At(2026, 11, 18, 10, 0));
        Assert.AreEqual(At(2026, 11, 19, 0, 0), next);
    }

    [TestMethod]
    public void StepOf15_From07_LandsOn15()
    {
        var next = CronExpression.Parse("*/15 * * * *").NextOccurrence(Cst, At(2026, 11, 18, 10, 7));
        Assert.AreEqual(At(2026, 11, 18, 10, 15), next);
    }

    [TestMethod]
    public void WeeklyMonday_FromWednesday_LandsOnNextMonday()
    {
        var next = CronExpression.Parse("0 9 * * 1").NextOccurrence(Cst, At(2026, 11, 18, 12, 0));
        Assert.AreEqual(At(2026, 11, 23, 9, 0), next);
        Assert.AreEqual(DayOfWeek.Monday, next!.Value.DayOfWeek);
    }

    [TestMethod]
    public void DayOfMonth31_SkipsApril_AndLandsOnMay31()
    {
        var next = CronExpression.Parse("0 0 31 * *").NextOccurrence(Cst, At(2026, 4, 1, 0, 0));
        Assert.AreEqual(At(2026, 5, 31, 0, 0), next);
    }

    [TestMethod]
    public void LeapDay_FindsNextLeapYear_NotTheSameYear()
    {
        var next = CronExpression.Parse("0 0 29 2 *").NextOccurrence(Cst, At(2026, 3, 1, 0, 0));
        Assert.AreEqual(At(2028, 2, 29, 0, 0), next);
    }

    [TestMethod]
    public void ImpossibleExpression_ReturnsNull()
    {
        // February never has 30 days, so the search window must expire.
        var next = CronExpression.Parse("0 0 30 2 *").NextOccurrence(Cst, At(2026, 1, 1, 0, 0));
        Assert.IsNull(next);
    }

    [TestMethod]
    public void DayOfMonthAndDayOfWeek_BothRestricted_MeansOr()
    {
        // "the 25th OR any Friday": from Nov 18 (Wed) the OR rule fires on Friday
        // Nov 20, while an AND rule would wait for a Friday that is also the 25th
        // (Dec 25). The case must pick a day where the two rules diverge: an earlier
        // attempt used "20 * 5" and both semantics landed on 11-20, so it proved nothing.
        var next = CronExpression.Parse("0 0 25 * 5").NextOccurrence(Cst, At(2026, 11, 18, 12, 0));
        Assert.AreEqual(At(2026, 11, 20, 0, 0), next);
    }

    [TestMethod]
    public void SundayAliasSeven_IsAccepted()
    {
        var viaSeven = CronExpression.Parse("0 9 * * 7").NextOccurrence(Cst, At(2026, 11, 18, 12, 0));
        Assert.AreEqual(DayOfWeek.Sunday, viaSeven!.Value.DayOfWeek);

        var viaZero = CronExpression.Parse("0 9 * * 0").NextOccurrence(Cst, At(2026, 11, 18, 12, 0));
        Assert.AreEqual(viaZero, viaSeven);
    }

    [TestMethod]
    public void SpringForwardGap_IsSkipped_NotReturned()
    {
        // 2026-03-08 02:30 does not exist in US Pacific time.
        TimeZoneInfo pacific;
        try { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { Assert.Inconclusive("Pacific time zone unavailable on this host."); return; }

        var after = new DateTimeOffset(2026, 3, 8, 0, 0, 0, pacific.GetUtcOffset(new DateTime(2026, 3, 8, 0, 0, 0)));
        var next = CronExpression.Parse("30 2 * * *").NextOccurrence(pacific, after);

        Assert.IsNotNull(next);
        var local = TimeZoneInfo.ConvertTime(next!.Value, pacific);
        Assert.AreEqual(9, local.Day, "the nonexistent 03-08 02:30 must be skipped");
        Assert.AreEqual(2, local.Hour);
        Assert.AreEqual(30, local.Minute);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("* * * *")]
    [DataRow("60 * * * *")]
    [DataRow("* 24 * * *")]
    [DataRow("abc * * * *")]
    [DataRow("*/0 * * * *")]
    [DataRow("5-1 * * * *")]
    public void InvalidExpressions_Throw(string expression)
    {
        Assert.ThrowsExactly<ArgumentException>(() => CronExpression.Parse(expression));
    }
}
