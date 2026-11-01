using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// 静默失效检测：把「表里 0 行」变成一个会被注意到的状态。
/// <para>
/// 两个方向都测 —— 只测"能报故障"会让正常状态被误报成红；
/// 只测"不误报"则退化成永远 Healthy，等于没做这件事。
/// </para>
/// </summary>
[TestClass]
public sealed class FactMemoryWriteMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static FactMemoryWriteSnapshot Snapshot(
        bool enabled = true,
        long count = 10,
        DateTimeOffset? last = null,
        int staleDays = 7) =>
        new(enabled, count, last ?? Now.AddDays(-1), Now, staleDays);

    [TestMethod]
    public void Disabled_is_not_an_alarm()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(enabled: false, count: 0));

        Assert.AreEqual(FactMemoryWriteHealth.Disabled, health);
        Assert.IsFalse(FactMemoryWriteMonitor.NeedsAttention(health),
            "没开启是预期状态，报成故障会让告警失去意义");
    }

    /// <summary>这条就是今天的真实情形：开关开了、表还是 0 行。</summary>
    [TestMethod]
    public void Enabled_but_empty_is_never_written()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 0));

        Assert.AreEqual(FactMemoryWriteHealth.NeverWritten, health);
        Assert.IsTrue(FactMemoryWriteMonitor.NeedsAttention(health));
    }

    [TestMethod]
    public void Recent_write_is_healthy()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddHours(-2)));

        Assert.AreEqual(FactMemoryWriteHealth.Healthy, health);
        Assert.IsFalse(FactMemoryWriteMonitor.NeedsAttention(health));
    }

    [TestMethod]
    public void Old_write_is_stalled()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddDays(-30)));

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, health);
    }

    [TestMethod]
    public void Boundary_exactly_at_threshold_is_still_healthy()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddDays(-7)));

        Assert.AreEqual(FactMemoryWriteHealth.Healthy, health, "刚好等于阈值不算超时");
    }

    [TestMethod]
    public void Rows_without_timestamp_is_stalled_because_it_cannot_be_trusted()
    {
        var snapshot = new FactMemoryWriteSnapshot(true, 5, LastWrittenAt: null, Now);

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, FactMemoryWriteMonitor.Evaluate(snapshot),
            "有行但说不出最后写入时间，说明观测链路本身有问题");
    }

    [TestMethod]
    public void Stale_days_below_one_is_clamped()
    {
        var snapshot = new FactMemoryWriteSnapshot(true, 5, Now.AddHours(-30), Now, StaleDays: 0);

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, FactMemoryWriteMonitor.Evaluate(snapshot));
    }

    [TestMethod]
    public void Every_state_has_a_description()
    {
        foreach (var health in Enum.GetValues<FactMemoryWriteHealth>())
        {
            var text = FactMemoryWriteMonitor.Describe(health);
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"状态 {health} 缺少说明");
        }
    }
}
