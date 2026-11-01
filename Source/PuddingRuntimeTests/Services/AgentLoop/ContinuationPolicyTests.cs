using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 自动续跑策略：不该停的时候别停，该停的时候必须停。
/// <para>
/// 两个方向都要测 —— 只测"会继续"会让它变成无限续跑（烧钱且掩盖卡住）；
/// 只测"会停"会让它退化成现在这样：任务没做完也停，等人拍一下。
/// </para>
/// </summary>
[TestClass]
public sealed class ContinuationPolicyTests
{
    private static ContinuationObservation Obs(
        AgentLoopStopReason reason = AgentLoopStopReason.Done,
        int openItems = 3,
        bool progress = true,
        int idle = 0,
        int roundsUsed = 5,
        int maxRounds = 50,
        bool userPending = false) => new()
    {
        StopReason = reason,
        OpenItems = openItems,
        LastRoundMadeProgress = progress,
        ConsecutiveIdleRounds = idle,
        RoundsUsed = roundsUsed,
        MaxRounds = maxRounds,
        UserMessagePending = userPending,
    };

    [TestMethod]
    public void Continues_when_work_remains_and_last_round_progressed()
    {
        var d = ContinuationPolicy.Default.Decide(Obs());

        Assert.AreEqual(ContinuationDecision.Continue, d,
            "目标没做完、预算还在、上一轮有推进 —— 这正是现在会错误停下的情形");
    }

    [TestMethod]
    public void Stops_when_all_items_are_done()
    {
        Assert.AreEqual(ContinuationDecision.Completed,
            ContinuationPolicy.Default.Decide(Obs(openItems: 0)));
    }

    [TestMethod]
    public void Stops_when_round_budget_is_exhausted()
    {
        Assert.AreEqual(ContinuationDecision.BudgetExhausted,
            ContinuationPolicy.Default.Decide(Obs(roundsUsed: 50, maxRounds: 50)));
    }

    [TestMethod]
    public void Stops_when_elapsed_or_token_budget_is_hit()
    {
        Assert.AreEqual(ContinuationDecision.BudgetExhausted,
            ContinuationPolicy.Default.Decide(Obs(reason: AgentLoopStopReason.MaxElapsedReached)));
        Assert.AreEqual(ContinuationDecision.BudgetExhausted,
            ContinuationPolicy.Default.Decide(Obs(reason: AgentLoopStopReason.BudgetExhausted)));
    }

    [TestMethod]
    public void Stops_after_repeated_idle_rounds()
    {
        var d = ContinuationPolicy.Default.Decide(Obs(idle: 2));

        Assert.AreEqual(ContinuationDecision.NoProgress, d,
            "连续两轮没进展必须停：空转续跑比停下来更糟，它烧钱还掩盖卡住");
    }

    [TestMethod]
    public void Stops_when_last_round_made_no_progress()
    {
        Assert.AreEqual(ContinuationDecision.NoProgress,
            ContinuationPolicy.Default.Decide(Obs(progress: false)));
    }

    [TestMethod]
    public void Pending_user_message_takes_priority_over_self_continuation()
    {
        var d = ContinuationPolicy.Default.Decide(Obs(userPending: true));

        Assert.AreEqual(ContinuationDecision.Halted, d,
            "用户来消息时优先处理用户，不要自顾自继续");
    }

    [DataTestMethod]
    [DataRow(AgentLoopStopReason.Cancelled)]
    [DataRow(AgentLoopStopReason.Waiting)]
    [DataRow(AgentLoopStopReason.Failed)]
    public void Halt_reasons_stop_immediately(AgentLoopStopReason reason)
    {
        Assert.AreEqual(ContinuationDecision.Halted,
            ContinuationPolicy.Default.Decide(Obs(reason: reason)));
    }

    [TestMethod]
    public void Reminder_only_when_continuing_with_open_items()
    {
        var policy = ContinuationPolicy.Default;

        Assert.IsTrue(policy.NeedsReminder(Obs()));
        Assert.IsFalse(policy.NeedsReminder(Obs(openItems: 0)));
        Assert.IsFalse(policy.NeedsReminder(Obs(progress: false)));
    }
}
