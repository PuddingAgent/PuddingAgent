using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 轮次账本：额度必须可审计。
/// <para>
/// 核心不变量：**只有真正进入模型历史的轮次才计数**。
/// 所以这里要同时测两个方向 —— 该计的必须计、不该计的绝不能计。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalRoundLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Reservation_alone_does_not_consume_a_round()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 3);

        ledger.TryReserve("r1", revision: 1, Now);

        Assert.AreEqual(0, ledger.RoundsStarted, "预约不是消耗：模型还没跑");
        Assert.AreEqual(1, ledger.PendingReservations.Count);
    }

    [TestMethod]
    public void Admitted_round_consumes_exactly_one()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 3);
        ledger.TryReserve("r1", 1, Now);

        Assert.IsTrue(ledger.TryAdmit("r1", "g1", currentRevision: 1));
        Assert.AreEqual(1, ledger.RoundsStarted);
        Assert.AreEqual(0, ledger.PendingReservations.Count);
    }

    [TestMethod]
    public void Stale_reservation_by_revision_does_not_consume()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 3);
        ledger.TryReserve("r1", revision: 1, Now);

        // 目标被编辑到 revision 2，旧预约作废
        Assert.IsFalse(ledger.TryAdmit("r1", "g1", currentRevision: 2));
        Assert.AreEqual(0, ledger.RoundsStarted, "过期预约不能吃掉额度");
    }

    [TestMethod]
    public void Reservation_for_another_goal_does_not_consume()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 3);
        ledger.TryReserve("r1", 1, Now);

        Assert.IsFalse(ledger.TryAdmit("r1", "g2", 1));
        Assert.AreEqual(0, ledger.RoundsStarted);
    }

    [TestMethod]
    public void Released_reservation_does_not_consume()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 3);
        ledger.TryReserve("r1", 1, Now);

        Assert.IsTrue(ledger.Release("r1"));
        Assert.IsFalse(ledger.TryAdmit("r1", "g1", 1), "已释放的预约为不存在");
        Assert.AreEqual(0, ledger.RoundsStarted);
    }

    [TestMethod]
    public void Exhausted_ledger_refuses_reservation_and_records_blocker_once()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 1);
        ledger.TryReserve("r1", 1, Now);
        ledger.TryAdmit("r1", "g1", 1);

        Assert.IsNull(ledger.TryReserve("r2", 1, Now), "耗尽后不再预约");
        Assert.IsNull(ledger.TryReserve("r3", 1, Now), "反复尝试也不预约");

        CollectionAssert.AreEqual(
            new[] { GoalRoundLedger.RoundLimitBlockerCode },
            ledger.Blockers.ToArray(),
            "阻塞码必须稳定且去重");
    }

    [TestMethod]
    public void Zero_round_goal_is_immediately_exhausted()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 0);

        Assert.IsTrue(ledger.IsExhausted);
        Assert.IsNull(ledger.TryReserve("r1", 1, Now));
        Assert.AreEqual(1, ledger.Blockers.Count);
    }

    [TestMethod]
    public void Invalidate_stale_clears_only_old_revisions()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 5);
        ledger.TryReserve("old", revision: 1, Now);
        ledger.TryReserve("new", revision: 2, Now);

        var invalidated = ledger.InvalidateStale(currentRevision: 2);

        Assert.AreEqual(1, invalidated);
        Assert.AreEqual(1, ledger.PendingReservations.Count);
        Assert.AreEqual("new", ledger.PendingReservations.Single().ReservationId);
    }

    [TestMethod]
    public void Same_reservation_id_is_idempotent()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 5);

        var a = ledger.TryReserve("r1", 1, Now);
        var b = ledger.TryReserve("r1", 1, Now.AddSeconds(5));

        Assert.AreSame(a, b, "重复预约同一 id 不应产生两条");
        Assert.AreEqual(1, ledger.PendingReservations.Count);
    }

    [TestMethod]
    public void Multiple_rounds_count_sequentially()
    {
        var ledger = new GoalRoundLedger("g1", maxRounds: 2);

        ledger.TryReserve("r1", 1, Now);
        ledger.TryAdmit("r1", "g1", 1);
        ledger.TryReserve("r2", 1, Now);
        ledger.TryAdmit("r2", "g1", 1);

        Assert.AreEqual(2, ledger.RoundsStarted);
        Assert.IsTrue(ledger.IsExhausted);
        Assert.IsNull(ledger.TryReserve("r3", 1, Now));
    }
}
