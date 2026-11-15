using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

/// <summary>
/// 子代理 → 父代理汇报的节流规则。
/// 关注点不是「能不能发」，而是「这一刻该不该说」：父代理的上下文窗口才是被消耗的一方。
/// </summary>
[TestClass]
public sealed class SubAgentProgressReportPolicyTests
{
    private static SubAgentProgressSnapshot Progress(int round, int toolCalls) =>
        new(round, toolCalls, SubAgentProgressKind.Progress);

    private static SubAgentProgressSnapshot Terminal(int round, SubAgentProgressKind kind) =>
        new(round, 0, kind);

    private static SubAgentProgressReportState AfterProgress(int round, int toolCalls) =>
        new(1, round, toolCalls, false);

    [TestMethod]
    public void FirstProgress_IsDelivered()
    {
        var plan = SubAgentProgressReportPolicy.Plan(
            SubAgentProgressReportState.Empty, Progress(1, 0));

        Assert.AreEqual(SubAgentProgressDecision.Deliver, plan.Decision);
        Assert.IsTrue(plan.ShouldDeliver);
    }

    [TestMethod]
    public void IdenticalSnapshot_IsSuppressedAsDuplicate()
    {
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(4, 7), Progress(4, 7));

        Assert.AreEqual(SubAgentProgressDecision.SuppressDuplicate, plan.Decision);
        Assert.IsFalse(plan.ShouldDeliver);
    }

    [TestMethod]
    public void SameRoundWithNewToolCalls_IsSuppressedAsTooFrequent()
    {
        // 同一轮里工具数变了，但轮次没推进 ⇒ 仍属「太频繁」，不该每调一次工具就说一句。
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(4, 7), Progress(4, 9));

        Assert.AreEqual(SubAgentProgressDecision.SuppressTooFrequent, plan.Decision);
    }

    [TestMethod]
    public void TwoRoundsAfterLastReport_IsSuppressed()
    {
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(4, 7), Progress(6, 20));

        Assert.AreEqual(SubAgentProgressDecision.SuppressTooFrequent, plan.Decision);
    }

    [TestMethod]
    public void ThreeRoundsAfterLastReport_IsDelivered()
    {
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(4, 7), Progress(7, 20));

        Assert.AreEqual(SubAgentProgressDecision.Deliver, plan.Decision);
    }

    [TestMethod]
    public void StaleRound_IsSuppressed()
    {
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(9, 30), Progress(5, 12));

        Assert.AreEqual(SubAgentProgressDecision.SuppressStale, plan.Decision);
    }

    [TestMethod]
    public void TerminalStatus_IsDeliveredEvenWhenIntervalNotReached()
    {
        var plan = SubAgentProgressReportPolicy.Plan(
            AfterProgress(4, 7), Terminal(4, SubAgentProgressKind.Completed));

        Assert.AreEqual(SubAgentProgressDecision.Deliver, plan.Decision);
    }

    [TestMethod]
    public void AfterTerminal_EverythingIsSuppressed()
    {
        var state = new SubAgentProgressReportState(3, 9, 30, TerminalDelivered: true);

        Assert.AreEqual(
            SubAgentProgressDecision.SuppressAfterTerminal,
            SubAgentProgressReportPolicy.Plan(state, Progress(20, 60)).Decision);
        Assert.AreEqual(
            SubAgentProgressDecision.SuppressAfterTerminal,
            SubAgentProgressReportPolicy.Plan(state, Terminal(20, SubAgentProgressKind.Failed)).Decision);
    }

    [TestMethod]
    public void ZeroInterval_IsClampedToOne()
    {
        // 配置成 0 不应退化成「每轮都发」。
        var plan = SubAgentProgressReportPolicy.Plan(AfterProgress(4, 7), Progress(5, 9), minRoundInterval: 0);

        Assert.AreEqual(SubAgentProgressDecision.Deliver, plan.Decision);
    }

    [TestMethod]
    public void Record_AfterProgressMovesCursor()
    {
        var next = SubAgentProgressReportPolicy.Record(
            SubAgentProgressReportState.Empty, Progress(5, 12));

        Assert.AreEqual(1, next.DeliveredCount);
        Assert.AreEqual(5, next.LastDeliveredRound);
        Assert.AreEqual(12, next.LastDeliveredToolCalls);
        Assert.IsFalse(next.TerminalDelivered);
    }

    [TestMethod]
    public void Record_AfterTerminalMarksTerminalDelivered()
    {
        var next = SubAgentProgressReportPolicy.Record(
            AfterProgress(5, 12), Terminal(6, SubAgentProgressKind.Failed));

        Assert.AreEqual(2, next.DeliveredCount);
        Assert.IsTrue(next.TerminalDelivered);
        // 终态不覆盖进度游标：事后仍能看出最后一条进度汇报停在哪。
        Assert.AreEqual(5, next.LastDeliveredRound);
    }

    [TestMethod]
    public void RecordThenPlan_ProducesSingleTerminalReport()
    {
        // 端到端串一遍：Progress → Terminal → 再来一条 Terminal，只有前两条应当送达。
        var state = SubAgentProgressReportState.Empty;

        var first = SubAgentProgressReportPolicy.Plan(state, Progress(1, 2));
        Assert.IsTrue(first.ShouldDeliver);
        state = SubAgentProgressReportPolicy.Record(state, Progress(1, 2));

        var second = SubAgentProgressReportPolicy.Plan(state, Terminal(2, SubAgentProgressKind.Completed));
        Assert.IsTrue(second.ShouldDeliver);
        state = SubAgentProgressReportPolicy.Record(state, Terminal(2, SubAgentProgressKind.Completed));

        var third = SubAgentProgressReportPolicy.Plan(state, Terminal(2, SubAgentProgressKind.Completed));
        Assert.IsFalse(third.ShouldDeliver, "终态只允许送达一次");

        Assert.AreEqual(2, state.DeliveredCount);
    }
}
