using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

/// <summary>
/// 按 run 归并回信额度的容器。
/// 关注点：额度必须跨同一 run 的多次工具调用累积（否则每次都从零开始，等于没有上限）；
/// 而淘汰只允许放宽、不允许收紧——若一次淘汰能让子代理永久报不回来，一个 bug 就足以静默掐断汇报通道。
/// </summary>
[TestClass]
public sealed class SubAgentParentReportLedgerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 11, 15, 13, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void SameRun_ReusesOneBudget_AndKeepsTheFirstConfiguration()
    {
        var ledger = new SubAgentParentReportLedger();

        var first = ledger.GetOrCreate("run-a", T0, maxMessages: 2, minInterval: TimeSpan.Zero);
        var second = ledger.GetOrCreate("run-a", T0.AddSeconds(1), maxMessages: 99, minInterval: TimeSpan.FromHours(1));

        Assert.AreSame(first, second);
        // 首次创建的配置生效：第二次传 99 不得把它抬高，否则只要多调一次就能扩额度。
        Assert.AreEqual(2, second.Remaining);
        Assert.AreEqual(1, ledger.Count);
    }

    [TestMethod]
    public void DifferentRuns_GetIndependentBudgets()
    {
        var ledger = new SubAgentParentReportLedger();

        var a = ledger.GetOrCreate("run-a", T0, maxMessages: 1, minInterval: TimeSpan.Zero);
        var b = ledger.GetOrCreate("run-b", T0, maxMessages: 1, minInterval: TimeSpan.Zero);

        Assert.AreNotSame(a, b);
        Assert.IsTrue(a.Commit(T0));
        Assert.AreEqual(0, a.Remaining);
        // 同一个容器里，一个 run 用尽不得影响另一个。
        Assert.AreEqual(1, b.Remaining);
        Assert.IsTrue(b.TryAcquire(T0).Allowed);
    }

    [TestMethod]
    public void Remove_DropsOnlyThatRun()
    {
        var ledger = new SubAgentParentReportLedger();
        ledger.GetOrCreate("run-a", T0);
        ledger.GetOrCreate("run-b", T0);

        Assert.IsTrue(ledger.Remove("run-a"));
        Assert.AreEqual(1, ledger.Count);
        Assert.IsFalse(ledger.Remove("run-a"), "第二次移除同一 run 应报不存在");
    }

    [TestMethod]
    public void IdleRun_IsReclaimed_WithoutTouchingActiveOnes()
    {
        var ledger = new SubAgentParentReportLedger(maxEntries: 8, entryTtl: TimeSpan.FromMinutes(1));

        var a = ledger.GetOrCreate("run-a", T0);
        var b = ledger.GetOrCreate("run-b", T0.AddSeconds(90));

        // run-a 已空闲 90 秒 > 60 秒 TTL，插入 run-b 时被回收；run-b 自己保留。
        Assert.AreEqual(1, ledger.Count);
        Assert.AreNotSame(a, ledger.GetOrCreate("run-a", T0.AddSeconds(120)));
        Assert.AreSame(b, ledger.GetOrCreate("run-b", T0.AddSeconds(120)));
    }

    [TestMethod]
    public void Cap_EvictsTheLeastRecentlyUsedRun()
    {
        var ledger = new SubAgentParentReportLedger(maxEntries: 2, entryTtl: TimeSpan.FromHours(1));

        var a = ledger.GetOrCreate("run-a", T0);
        var b = ledger.GetOrCreate("run-b", T0.AddSeconds(1));
        _ = ledger.GetOrCreate("run-c", T0.AddSeconds(2));

        Assert.AreEqual(2, ledger.Count);
        Assert.AreSame(b, ledger.GetOrCreate("run-b", T0.AddSeconds(3)));
        Assert.AreNotSame(a, ledger.GetOrCreate("run-a", T0.AddSeconds(4)));
    }

    [TestMethod]
    public void Eviction_FailsOpen_SoTheNewRunStartsWithFullQuota()
    {
        var ledger = new SubAgentParentReportLedger(maxEntries: 1, entryTtl: TimeSpan.FromHours(1));

        var exhausted = ledger.GetOrCreate("run-a", T0, maxMessages: 1, minInterval: TimeSpan.Zero);
        Assert.IsTrue(exhausted.Commit(T0));
        Assert.AreEqual(0, exhausted.Remaining);

        // 容器满、run-b 挤掉 run-a。淘汰的后果只能是「那个 run 重新有额度」，
        // 绝不能是「新 run 一上来就超额」。
        var fresh = ledger.GetOrCreate("run-b", T0.AddSeconds(1), maxMessages: 1, minInterval: TimeSpan.Zero);

        Assert.AreEqual(1, fresh.Remaining);
        Assert.IsTrue(fresh.TryAcquire(T0.AddSeconds(1)).Allowed);
    }
}
