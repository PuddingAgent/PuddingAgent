using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;

namespace PuddingLlmUsageTests;

/// <summary>
/// 消耗判定规则。每个用例都在守一条**不能被放宽**的规则：
/// 无法判定时必须是 <c>null</c>，绝不能落成 0 —— 0 会被读成「这段时间没花钱」。
/// </summary>
[TestClass]
public sealed class LlmUsageAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static LlmBalanceObservation Ok(int minutes, decimal balance) =>
        LlmBalanceObservation.Success("deepseek", "CNY", balance, At(minutes));

    [TestMethod]
    public void DownwardBalance_IsConsumption_WithExactAmount()
    {
        var intervals = LlmUsageAnalyzer.Analyze([Ok(0, 248.55m), Ok(10, 248.05m)]);

        Assert.AreEqual(1, intervals.Count);
        var interval = intervals[0];
        Assert.AreEqual(LlmUsageIntervalKind.Consumption, interval.Kind);
        Assert.AreEqual(-0.5m, interval.BalanceDelta);
        Assert.AreEqual(0.5m, interval.Consumption);
        Assert.AreEqual(TimeSpan.FromMinutes(10), interval.Duration);
    }

    [TestMethod]
    public void UpwardBalance_IsRecharge_AndConsumptionStaysNull()
    {
        // 充值会让余额上升。若直接做差得到「负消耗」，曲线与汇总都会反向。
        var intervals = LlmUsageAnalyzer.Analyze([Ok(0, 100m), Ok(10, 300m)]);

        Assert.AreEqual(LlmUsageIntervalKind.Recharge, intervals[0].Kind);
        Assert.AreEqual(200m, intervals[0].BalanceDelta);
        Assert.IsNull(intervals[0].Consumption, "充值区间的消耗必须为 null，不能是 0 也不能是负数。");
    }

    [TestMethod]
    public void FailedEndpoint_IsUnavailable_NotZero()
    {
        var observations = new[]
        {
            Ok(0, 100m),
            LlmBalanceObservation.Failure("deepseek", "CNY", "HTTP 503", At(10)),
            Ok(20, 99m),
        };

        var intervals = LlmUsageAnalyzer.Analyze(observations);

        Assert.AreEqual(2, intervals.Count);
        Assert.AreEqual(LlmUsageIntervalKind.Unavailable, intervals[0].Kind);
        Assert.IsNull(intervals[0].Consumption);
        StringAssert.Contains(intervals[0].Note, "HTTP 503");
        Assert.AreEqual(LlmUsageIntervalKind.Unavailable, intervals[1].Kind);
        Assert.IsNull(intervals[1].Consumption);
    }

    [TestMethod]
    public void IntervalOverGapThreshold_IsGap_EvenWhenBalanceFell()
    {
        // 45 分钟 > 默认 30 分钟阈值：中间可能漏采到充值，此时"余额下降"不能直接当消耗。
        var intervals = LlmUsageAnalyzer.Analyze([Ok(0, 100m), Ok(45, 90m)]);

        Assert.AreEqual(LlmUsageIntervalKind.Gap, intervals[0].Kind);
        Assert.AreEqual(-10m, intervals[0].BalanceDelta, "差值仍要如实给出，供读者自行判断。");
        Assert.IsNull(intervals[0].Consumption, "缺口区间的消耗必须为 null。");
    }

    [TestMethod]
    public void IntervalAtExactThreshold_IsStillDecidable()
    {
        // 边界必须落在"可判定"一侧：30 分钟恰好等于阈值时不应判为缺口。
        var intervals = LlmUsageAnalyzer.Analyze([Ok(0, 100m), Ok(30, 99m)]);

        Assert.AreEqual(LlmUsageIntervalKind.Consumption, intervals[0].Kind);
        Assert.AreEqual(1m, intervals[0].Consumption);
    }

    [TestMethod]
    public void EqualBalance_IsNoChange_AndDoesNotClaimZeroSpending()
    {
        var intervals = LlmUsageAnalyzer.Analyze([Ok(0, 100m), Ok(10, 100m)]);

        Assert.AreEqual(LlmUsageIntervalKind.NoChange, intervals[0].Kind);
        Assert.AreEqual(0m, intervals[0].Consumption);
        StringAssert.Contains(intervals[0].Note, "不等于期间没有调用");
    }

    [TestMethod]
    public void SingleObservation_YieldsNoIntervals()
    {
        Assert.AreEqual(0, LlmUsageAnalyzer.Analyze([Ok(0, 100m)]).Count);
        Assert.AreEqual(0, LlmUsageAnalyzer.Analyze([]).Count);
    }

    [TestMethod]
    public void DifferentProviders_AreNeverSubtractedAgainstEachOther()
    {
        var observations = new[]
        {
            LlmBalanceObservation.Success("deepseek", "CNY", 100m, At(0)),
            LlmBalanceObservation.Success("bigmodel", "CNY", 900m, At(10)),
            LlmBalanceObservation.Success("deepseek", "CNY", 99m, At(20)),
        };

        var intervals = LlmUsageAnalyzer.Analyze(observations);

        Assert.AreEqual(1, intervals.Count, "两个 provider 各只有一个点，不该互相做差。");
        Assert.AreEqual("deepseek", intervals[0].ProviderId);
        Assert.AreEqual(1m, intervals[0].Consumption);
    }

    [TestMethod]
    public void DifferentCurrencies_AreNeverSubtractedAgainstEachOther()
    {
        var observations = new[]
        {
            LlmBalanceObservation.Success("deepseek", "CNY", 100m, At(0)),
            LlmBalanceObservation.Success("deepseek", "USD", 900m, At(10)),
            LlmBalanceObservation.Success("deepseek", "CNY", 99m, At(20)),
        };

        var intervals = LlmUsageAnalyzer.Analyze(observations);

        Assert.AreEqual(1, intervals.Count, "币种不同的样本不得跨币种做差。");
        Assert.AreEqual("CNY", intervals[0].Currency);
    }

    [TestMethod]
    public void UnorderedInput_IsSortedBeforePairing()
    {
        var intervals = LlmUsageAnalyzer.Analyze([Ok(20, 98m), Ok(0, 100m), Ok(10, 99m)]);

        Assert.AreEqual(2, intervals.Count);
        Assert.AreEqual(1m, intervals[0].Consumption);
        Assert.AreEqual(1m, intervals[1].Consumption);
        Assert.IsTrue(intervals[0].FromUtc < intervals[1].FromUtc);
    }

    [TestMethod]
    public void NonPositiveGapThreshold_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LlmUsageAnalyzer.Analyze([Ok(0, 1m)], new LlmUsageAnalysisOptions(TimeSpan.Zero, 0m)));
    }

    [TestMethod]
    public void NegativeTolerance_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LlmUsageAnalyzer.Analyze([Ok(0, 1m)], new LlmUsageAnalysisOptions(TimeSpan.FromMinutes(30), -1m)));
    }

    // ── 汇总 ────────────────────────────────────────────────────────────────

    private static LlmUsageInterval Interval(
        int fromMinute,
        int toMinute,
        decimal? consumption,
        LlmUsageIntervalKind kind)
        => new("deepseek", "CNY", At(fromMinute), At(toMinute), TimeSpan.FromMinutes(toMinute - fromMinute),
            BalanceDelta: consumption is null ? null : -consumption.Value,
            Consumption: consumption,
            kind,
            "test");

    [TestMethod]
    public void Summarize_ExcludesUndecidableIntervalsFromBothTotalAndDenominator()
    {
        var intervals = new[]
        {
            Interval(0, 10, 1m, LlmUsageIntervalKind.Consumption),
            Interval(10, 20, null, LlmUsageIntervalKind.Gap),          // 10 分钟不计入
            Interval(20, 30, null, LlmUsageIntervalKind.Unavailable),  // 10 分钟不计入
            Interval(30, 40, 3m, LlmUsageIntervalKind.Consumption),
        };

        var summary = LlmUsageAnalyzer.Summarize(intervals, At(0), At(40)).Single();

        Assert.AreEqual(4, summary.IntervalCount);
        Assert.AreEqual(2, summary.DecidableIntervalCount);
        Assert.AreEqual(1, summary.GapCount);
        Assert.AreEqual(1, summary.UnavailableCount);
        Assert.AreEqual(4m, summary.TotalConsumption, "只累加可判定区间的消耗。");
        Assert.AreEqual(TimeSpan.FromMinutes(20), summary.DecidableDuration,
            "分母只能用可判定时长，否则会算出偏低的每小时消耗。");
        Assert.AreEqual(12m, summary.ConsumptionPerHour, "4 元 / (20/60) 小时 = 12。");
    }

    [TestMethod]
    public void Summarize_WithNoDecidableInterval_ReportsNullRate_NotZero()
    {
        var intervals = new[] { Interval(0, 10, null, LlmUsageIntervalKind.Gap) };

        var summary = LlmUsageAnalyzer.Summarize(intervals, At(0), At(10)).Single();

        Assert.IsNull(summary.TotalConsumption);
        Assert.IsNull(summary.ConsumptionPerHour, "没有任何可判定区间时，速率必须是 null，不是 0。");
    }

    [TestMethod]
    public void Summarize_ExcludesIntervalsCrossingTheWindowBoundary()
    {
        var intervals = new[]
        {
            Interval(0, 10, 1m, LlmUsageIntervalKind.Consumption),
            Interval(5, 25, 2m, LlmUsageIntervalKind.Consumption), // 跨边界，必须排除
        };

        var summary = LlmUsageAnalyzer.Summarize(intervals, At(5), At(25)).Single();

        Assert.AreEqual(1, summary.IntervalCount, "跨越窗口边界的区间会被裁成虚假的精确值，只能排除。");
        Assert.AreEqual(2m, summary.TotalConsumption);
    }

    [TestMethod]
    public void Summarize_RejectsInvertedWindow()
    {
        Assert.Throws<ArgumentException>(
            () => LlmUsageAnalyzer.Summarize([], At(10), At(0)));
    }

    // ── 组件边界 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void PuddingLlmUsage_AssemblyReferencesNoProjectAssemblies()
    {
        var allowed = new[] { "System", "Microsoft", "netstandard", "mscorlib", "WindowsBase" };

        var offenders = typeof(LlmUsageAnalyzer).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .Where(name => !allowed.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.AreEqual(
            0,
            offenders.Length,
            "PuddingLlmUsage 必须是零工程依赖的叶子；实际引用：" + string.Join(", ", offenders));
    }
}
