#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// SSE 大差额恢复预算计量器（Plan §4.3；冻结合同 <see cref="SseCatchUpMeter"/> 的 XML 文档）。
/// </summary>
[TestClass]
public sealed class SseCatchUpMeterTests
{
    [TestMethod]
    public void DefaultBudget_ConsumesWithinLimits()
    {
        var meter = new SseCatchUpMeter();

        for (var i = 0; i < 100; i++)
            Assert.IsTrue(meter.TryConsume(10, TimeSpan.FromMilliseconds(i)), $"event {i} should be consumed");

        Assert.AreEqual(100, meter.EventsConsumed);
        Assert.AreEqual(1_000L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.None, meter.StopReason);
    }

    [TestMethod]
    public void EventLimit_RejectsEventBeyondBudgetWithoutConsumingIt()
    {
        var meter = new SseCatchUpMeter();

        for (var i = 0; i < 2_000; i++)
            Assert.IsTrue(meter.TryConsume(0, TimeSpan.Zero));

        Assert.IsFalse(meter.TryConsume(0, TimeSpan.Zero), "第 2001 条必须拒绝");

        Assert.AreEqual(2_000, meter.EventsConsumed);
        Assert.AreEqual(0L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.EventLimit, meter.StopReason);
    }

    [TestMethod]
    public void EventLimit_UsesConfiguredBudget()
    {
        var meter = new SseCatchUpMeter(new ConversationReadModelLimits { SseCatchUpMaxEvents = 2 });

        Assert.IsTrue(meter.TryConsume(1, TimeSpan.Zero));
        Assert.IsTrue(meter.TryConsume(1, TimeSpan.Zero));
        Assert.IsFalse(meter.TryConsume(1, TimeSpan.Zero));

        Assert.AreEqual(2, meter.EventsConsumed);
        Assert.AreEqual(SseCatchUpStopReason.EventLimit, meter.StopReason);
    }

    [TestMethod]
    public void ByteLimit_RejectsOversizedEventWithoutConsumingIt()
    {
        var meter = new SseCatchUpMeter(new ConversationReadModelLimits { SseCatchUpMaxBytes = 100 });

        Assert.IsTrue(meter.TryConsume(60, TimeSpan.Zero));
        Assert.IsFalse(meter.TryConsume(50, TimeSpan.Zero), "超出字节预算的事件不得计入消费量");

        Assert.AreEqual(1, meter.EventsConsumed);
        Assert.AreEqual(60L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.ByteLimit, meter.StopReason);
    }

    [TestMethod]
    public void ByteLimit_DefaultBudgetRejectsSingleOversizedEvent()
    {
        var meter = new SseCatchUpMeter();

        Assert.IsFalse(meter.TryConsume(2 * 1024 * 1024 + 1, TimeSpan.Zero));

        Assert.AreEqual(0, meter.EventsConsumed);
        Assert.AreEqual(0L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.ByteLimit, meter.StopReason);
    }

    [TestMethod]
    public void TimeBudget_RejectsWhenElapsedExceedsBudget()
    {
        var meter = new SseCatchUpMeter(new ConversationReadModelLimits { SseCatchUpMaxDuration = TimeSpan.FromMilliseconds(10) });

        Assert.IsTrue(meter.TryConsume(1, TimeSpan.FromMilliseconds(5)));
        Assert.IsTrue(meter.TryConsume(1, TimeSpan.FromMilliseconds(10)));
        Assert.IsFalse(meter.TryConsume(1, TimeSpan.FromMilliseconds(11)), "耗时超预算的事件不得消费");

        Assert.AreEqual(2, meter.EventsConsumed);
        Assert.AreEqual(2L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.TimeBudget, meter.StopReason);
    }

    [TestMethod]
    public void TimeBudget_DefaultBudgetRejectsAfterOneSecond()
    {
        var meter = new SseCatchUpMeter();

        Assert.IsFalse(meter.TryConsume(1, TimeSpan.FromMilliseconds(1_001)));

        Assert.AreEqual(0, meter.EventsConsumed);
        Assert.AreEqual(SseCatchUpStopReason.TimeBudget, meter.StopReason);
    }

    [TestMethod]
    public void AfterStop_FurtherCallsReturnFalseWithoutCounting()
    {
        var meter = new SseCatchUpMeter(new ConversationReadModelLimits { SseCatchUpMaxEvents = 1 });

        Assert.IsTrue(meter.TryConsume(5, TimeSpan.Zero));
        Assert.IsFalse(meter.TryConsume(5, TimeSpan.Zero));

        Assert.IsFalse(meter.TryConsume(5, TimeSpan.Zero));
        Assert.IsFalse(meter.TryConsume(0, TimeSpan.Zero));

        Assert.AreEqual(1, meter.EventsConsumed);
        Assert.AreEqual(5L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.EventLimit, meter.StopReason);
    }

    [TestMethod]
    public void ZeroByteEvent_IsConsumed()
    {
        var meter = new SseCatchUpMeter();

        Assert.IsTrue(meter.TryConsume(0, TimeSpan.Zero));

        Assert.AreEqual(1, meter.EventsConsumed);
        Assert.AreEqual(0L, meter.BytesConsumed);
    }

    [TestMethod]
    public void EventLimitAndByteLimitBothTriggered_ReportsEventLimitFirst()
    {
        var meter = new SseCatchUpMeter(new ConversationReadModelLimits
        {
            SseCatchUpMaxEvents = 1,
            SseCatchUpMaxBytes = 10,
        });

        Assert.IsTrue(meter.TryConsume(5, TimeSpan.Zero));
        Assert.IsFalse(meter.TryConsume(20, TimeSpan.Zero), "第 2 条同时触顶事件数与字节预算，按固定顺序先报事件数");

        Assert.AreEqual(1, meter.EventsConsumed);
        Assert.AreEqual(5L, meter.BytesConsumed);
        Assert.AreEqual(SseCatchUpStopReason.EventLimit, meter.StopReason);
    }

    [TestMethod]
    public void NegativeEventBytes_Throws()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SseCatchUpMeter().TryConsume(-1, TimeSpan.Zero));

    [TestMethod]
    public void NegativeElapsed_Throws()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SseCatchUpMeter().TryConsume(1, TimeSpan.FromMilliseconds(-1)));

    [TestMethod]
    public void ElapsedGoingBackwards_Throws()
    {
        var meter = new SseCatchUpMeter();
        Assert.IsTrue(meter.TryConsume(1, TimeSpan.FromMilliseconds(10)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => meter.TryConsume(1, TimeSpan.FromMilliseconds(5)));
    }

    [TestMethod]
    public void SseCatchUpStopReason_FrozenEnumValues()
    {
        Assert.AreEqual(0, (int)SseCatchUpStopReason.None);
        Assert.AreEqual(1, (int)SseCatchUpStopReason.EventLimit);
        Assert.AreEqual(2, (int)SseCatchUpStopReason.ByteLimit);
        Assert.AreEqual(3, (int)SseCatchUpStopReason.TimeBudget);
    }
}
