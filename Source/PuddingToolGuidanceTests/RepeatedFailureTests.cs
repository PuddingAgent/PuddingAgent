using PuddingToolGuidance;
using PuddingToolGuidance.Rules;

namespace PuddingToolGuidanceTests;

/// <summary>
/// 低效循环检测：连续失败到阈值才提示，成功即清零，过期即清零。
/// <para>
/// 这条规则最容易出两种错：① 只失败 1 次就叫「循环」（变成噪声）；② 失败十次也不叫（等于摆设）。
/// 因此正反例必须贴着实测计数来写。
/// </para>
/// </summary>
[TestClass]
public sealed class RepeatedFailureTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now += delta;
    }

    private static GuidanceInput Input(int count) =>
        new("shell", "cmd", "dir", Succeeded: count == 0, ConsecutiveFailureCount: count);

    // ── 规则本身 ───────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Below_threshold_does_not_advise(int count)
    {
        var rule = new RepeatedFailureRule();
        Assert.IsNull(rule.Evaluate(Input(count)), $"连续失败 {count} 次不该提示");
    }

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(7)]
    public void At_or_above_threshold_advises(int count)
    {
        var rule = new RepeatedFailureRule();
        var advice = rule.Evaluate(Input(count));

        Assert.IsNotNull(advice);
        Assert.AreEqual("loop.repeated_failure", advice!.RuleId);
        StringAssert.Contains(advice.Message, "--blame-hang", "提示要给出具体替代手段，而不是空泛劝退");
    }

    // ── 计数器 ─────────────────────────────────────────────────

    [TestMethod]
    public void Counter_accumulates_until_success_resets()
    {
        var tracker = new ConsecutiveFailureTracker();

        Assert.AreEqual(1, tracker.Register("k", succeeded: false));
        Assert.AreEqual(2, tracker.Register("k", succeeded: false));
        Assert.AreEqual(3, tracker.Register("k", succeeded: false));
        Assert.AreEqual(0, tracker.Register("k", succeeded: true), "成功必须清零");
        Assert.AreEqual(1, tracker.Register("k", succeeded: false), "清零后重新从 1 开始");
    }

    [TestMethod]
    public void Counter_is_per_key()
    {
        var tracker = new ConsecutiveFailureTracker();

        tracker.Register("a", succeeded: false);
        tracker.Register("a", succeeded: false);

        Assert.AreEqual(0, tracker.Peek("b"), "不同 key 不得互相污染");
        Assert.AreEqual(2, tracker.Peek("a"));
    }

    [TestMethod]
    public void Counter_resets_after_window_expires()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new ConsecutiveFailureTracker(TimeSpan.FromMinutes(10), timeProvider: clock);

        tracker.Register("k", succeeded: false);
        tracker.Register("k", succeeded: false);
        Assert.AreEqual(2, tracker.Peek("k"));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.AreEqual(0, tracker.Peek("k"), "过期条目在查询时视为 0");
        Assert.AreEqual(1, tracker.Register("k", succeeded: false), "过期后再失败从 1 重新计");
    }

    [TestMethod]
    public void Counter_evicts_expired_entries_when_full()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new ConsecutiveFailureTracker(TimeSpan.FromMinutes(5), capacity: 16, timeProvider: clock);

        for (var i = 0; i < 16; i++)
            tracker.Register($"old-{i}", succeeded: false);

        clock.Advance(TimeSpan.FromMinutes(6));
        tracker.Register("new", succeeded: false);

        Assert.IsTrue(tracker.Count <= 16, $"容量上限必须守住，实际 {tracker.Count}");
        Assert.AreEqual(0, tracker.Peek("old-0"), "过期条目应被清掉");
        Assert.AreEqual(1, tracker.Peek("new"));
    }
}
