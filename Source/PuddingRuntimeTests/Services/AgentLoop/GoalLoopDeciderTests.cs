using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 统一决策入口：四个判定（生命周期 / 轮次 / 续跑 / 空转）编排成**一个动作**。
/// <para>
/// 这里重点测的是**顺序**，不是单条规则 —— 单条规则各自已有测试。
/// 顺序错了的典型后果：用户说话时还在自顾自续跑；或声称完成却没证据却被放行。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalLoopDeciderTests
{
    private static GoalState Active() => GoalLifecycle.Create("g1", "接完记忆写入链路", maxRounds: 8);

    private static GoalLoopInput Input(
        GoalState? goal = null,
        int openItems = 3,
        bool progress = true,
        int idle = 0,
        int roundsStarted = 0,
        bool userPending = false,
        bool loopDetected = false,
        bool claimsComplete = false,
        IReadOnlyList<GoalEvidence>? evidence = null,
        AgentLoopStopReason stop = AgentLoopStopReason.Done) => new()
    {
        Goal = goal ?? Active(),
        OpenItems = openItems,
        LastRoundMadeProgress = progress,
        ConsecutiveIdleRounds = idle,
        RoundsStarted = roundsStarted,
        UserMessagePending = userPending,
        LoopDetected = loopDetected,
        ClaimsComplete = claimsComplete,
        CompletionEvidence = evidence,
        StopReason = stop,
    };

    private static IReadOnlyList<GoalEvidence> GoodEvidence() =>
    [
        new GoalEvidence(GoalEvidenceKind.Test, "GoalLoopDeciderTests", "全绿"),
    ];

    [TestMethod]
    public void User_message_wins_over_everything_else()
    {
        // 即便同时满足"继续"的所有条件，人说话也要让位
        var outcome = GoalLoopDecider.Decide(Input(userPending: true));

        Assert.AreEqual(GoalLoopActionKind.Yield, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonUserPending, outcome.ReasonCode);
    }

    [TestMethod]
    public void Claim_of_completion_without_evidence_is_blocked_not_trusted()
    {
        var outcome = GoalLoopDecider.Decide(Input(claimsComplete: true, evidence: null));

        Assert.AreEqual(GoalLoopActionKind.Block, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonEvidenceMissing, outcome.ReasonCode);
    }

    [TestMethod]
    public void Claim_of_completion_with_unverifiable_evidence_is_blocked()
    {
        var weak = new[] { new GoalEvidence(GoalEvidenceKind.File, "", "看起来好了") };

        var outcome = GoalLoopDecider.Decide(Input(claimsComplete: true, evidence: weak));

        Assert.AreEqual(GoalLoopActionKind.Block, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonEvidenceMissing, outcome.ReasonCode);
    }

    [TestMethod]
    public void Claim_of_completion_with_evidence_completes()
    {
        var outcome = GoalLoopDecider.Decide(Input(claimsComplete: true, evidence: GoodEvidence()));

        Assert.AreEqual(GoalLoopActionKind.Complete, outcome.Action);
        Assert.IsTrue(outcome.IsTerminal);
    }

    [TestMethod]
    public void Disarmed_goal_stops_even_if_work_remains()
    {
        var restarted = GoalLifecycle.AfterRestart(Active());

        var outcome = GoalLoopDecider.Decide(Input(goal: restarted));

        Assert.AreEqual(GoalLoopActionKind.Stop, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonDisarmed, outcome.ReasonCode);
    }

    [TestMethod]
    public void Loop_detection_blocks_before_round_limit()
    {
        var outcome = GoalLoopDecider.Decide(Input(loopDetected: true, roundsStarted: 7));

        Assert.AreEqual(GoalLoopActionKind.Block, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonLoopDetected, outcome.ReasonCode);
    }

    [TestMethod]
    public void Round_limit_blocks_with_stable_code()
    {
        var outcome = GoalLoopDecider.Decide(Input(roundsStarted: 8));

        Assert.AreEqual(GoalLoopActionKind.Block, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonRoundLimit, outcome.ReasonCode);
    }

    [TestMethod]
    public void Empty_checklist_continues_to_gather_evidence_instead_of_stopping()
    {
        // 清单空了 ≠ 做完了：让它去收尾（拿证据 / 标完成），而不是停在那里
        var outcome = GoalLoopDecider.Decide(Input(openItems: 0));

        Assert.AreEqual(GoalLoopActionKind.Continue, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonNothingToDo, outcome.ReasonCode);
    }

    [TestMethod]
    public void No_progress_stops_to_avoid_burning_tokens()
    {
        var outcome = GoalLoopDecider.Decide(Input(progress: false));

        Assert.AreEqual(GoalLoopActionKind.Stop, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonNoProgress, outcome.ReasonCode);
    }

    [DataTestMethod]
    [DataRow(AgentLoopStopReason.Cancelled)]
    [DataRow(AgentLoopStopReason.Waiting)]
    [DataRow(AgentLoopStopReason.Failed)]
    public void Halt_reasons_stop(AgentLoopStopReason reason)
    {
        var outcome = GoalLoopDecider.Decide(Input(stop: reason));

        Assert.AreEqual(GoalLoopActionKind.Stop, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonStopped, outcome.ReasonCode);
    }

    [DataTestMethod]
    [DataRow(AgentLoopStopReason.BudgetExhausted)]
    [DataRow(AgentLoopStopReason.MaxElapsedReached)]
    public void Budget_reasons_block(AgentLoopStopReason reason)
    {
        var outcome = GoalLoopDecider.Decide(Input(stop: reason));

        Assert.AreEqual(GoalLoopActionKind.Block, outcome.Action);
    }

    [TestMethod]
    public void All_conditions_met_continues()
    {
        var outcome = GoalLoopDecider.Decide(Input());

        Assert.AreEqual(GoalLoopActionKind.Continue, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonProgress, outcome.ReasonCode);
        Assert.IsFalse(outcome.IsTerminal);
    }

    [TestMethod]
    public void Completed_goal_does_not_restart()
    {
        var done = GoalLifecycle.Complete(Active(), GoodEvidence());

        var outcome = GoalLoopDecider.Decide(Input(goal: done, claimsComplete: false));

        Assert.AreEqual(GoalLoopActionKind.Stop, outcome.Action);
        Assert.AreEqual(GoalLoopDecider.ReasonAlreadyComplete, outcome.ReasonCode);
    }
}
