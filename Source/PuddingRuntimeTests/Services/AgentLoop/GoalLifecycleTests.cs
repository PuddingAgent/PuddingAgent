using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 生命周期状态机：phase 是持久的，activation 是进程本地的。
/// <para>
/// 这个分离的全部价值在于「重启后不自动跑」——所以必须有一条测试专门证明它，
/// 而不是靠代码里某处 if 兜住。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalLifecycleTests
{
    private static GoalState Created() => GoalLifecycle.Create("g1", "把记忆库接上", maxRounds: 8);

    private static IReadOnlyList<GoalEvidence> Evidence() =>
    [
        new GoalEvidence(GoalEvidenceKind.Test, "RepeatedActionGuardTests", "7/7 通过"),
    ];

    [TestMethod]
    public void Create_starts_active_and_armed()
    {
        var g = Created();

        Assert.AreEqual(GoalPhase.Active, g.Phase);
        Assert.AreEqual(GoalActivation.Armed, g.Activation, "模型建的目标默认自动续跑");
        Assert.AreEqual(1, g.Revision);
        Assert.IsTrue(GoalLifecycle.CanAutoContinue(g));
    }

    [TestMethod]
    public void Pause_keeps_phase_paused_and_disarms()
    {
        var g = GoalLifecycle.Pause(Created());

        Assert.AreEqual(GoalPhase.Paused, g.Phase);
        Assert.AreEqual(GoalActivation.Disarmed, g.Activation);
        Assert.IsFalse(GoalLifecycle.CanAutoContinue(g));
        Assert.AreEqual(2, g.Revision, "每次变更 revision 必须 +1（CAS 身份）");
    }

    [TestMethod]
    public void Resume_rearms_from_paused_and_from_blocked()
    {
        var fromPaused = GoalLifecycle.Resume(GoalLifecycle.Pause(Created()));
        Assert.AreEqual(GoalPhase.Active, fromPaused.Phase);
        Assert.AreEqual(GoalActivation.Armed, fromPaused.Activation);

        var blocked = GoalLifecycle.Block(Created(), "round-limit", "用完了 8 轮");
        var fromBlocked = GoalLifecycle.Resume(blocked);
        Assert.AreEqual(GoalPhase.Active, fromBlocked.Phase);
        Assert.AreEqual(GoalActivation.Armed, fromBlocked.Activation);
        Assert.IsNull(fromBlocked.BlockCode, "恢复后必须清掉阻塞原因");
    }

    [TestMethod]
    public void Complete_disarms_and_cannot_complete_twice()
    {
        var done = GoalLifecycle.Complete(Created(), Evidence());

        Assert.AreEqual(GoalPhase.Complete, done.Phase);
        Assert.AreEqual(GoalActivation.Disarmed, done.Activation);
        Assert.ThrowsExactly<GoalTransitionException>(() => GoalLifecycle.Complete(done, Evidence()));
    }

    [TestMethod]
    public void Block_requires_lower_kebab_code_and_message()
    {
        var g = Created();

        var bad = Assert.ThrowsExactly<GoalTransitionException>(
            () => GoalLifecycle.Block(g, "RoundLimit", "x"));
        Assert.AreEqual(GoalLifecycle.InvalidBlockReason, bad.Code);

        var empty = Assert.ThrowsExactly<GoalTransitionException>(
            () => GoalLifecycle.Block(g, "round-limit", "  "));
        Assert.AreEqual(GoalLifecycle.InvalidBlockReason, empty.Code);
    }

    [TestMethod]
    public void Block_records_reason_and_disarms()
    {
        var blocked = GoalLifecycle.Block(Created(), "round-limit", "达到 8 轮上限");

        Assert.AreEqual(GoalPhase.Blocked, blocked.Phase);
        Assert.AreEqual("round-limit", blocked.BlockCode);
        Assert.AreEqual("达到 8 轮上限", blocked.BlockMessage);
        Assert.AreEqual(GoalActivation.Disarmed, blocked.Activation);
    }

    /// <summary>
    /// 这是整个设计的核心断言：重启**不改 phase**，只把 activation 打回 Disarmed。
    /// </summary>
    [TestMethod]
    public void After_restart_active_goal_is_disarmed()
    {
        var before = Created();
        Assert.IsTrue(GoalLifecycle.CanAutoContinue(before));

        var after = GoalLifecycle.AfterRestart(before);

        Assert.AreEqual(GoalPhase.Active, after.Phase, "phase 是持久的，重启不该改它");
        Assert.AreEqual(GoalActivation.Disarmed, after.Activation, "activation 是进程本地的，重启必须回到 disarmed");
        Assert.IsFalse(GoalLifecycle.CanAutoContinue(after), "重启后不得自动续跑");
        Assert.AreEqual(before.Revision, after.Revision, "重启不是一次变更，不该动 revision");
    }

    [DataTestMethod]
    [DataRow("pause")]
    [DataRow("complete")]
    [DataRow("block")]
    public void Invalid_transition_from_complete_is_rejected(string operation)
    {
        var done = GoalLifecycle.Complete(Created(), Evidence());

        var ex = Assert.ThrowsExactly<GoalTransitionException>(() => operation switch
        {
            "pause" => GoalLifecycle.Pause(done),
            "complete" => GoalLifecycle.Complete(done, Evidence()),
            _ => GoalLifecycle.Block(done, "some-code", "x"),
        });

        Assert.AreEqual(GoalLifecycle.InvalidTransition, ex.Code);
    }

    /// <summary>
    /// 这一条是整个「完成证据链」的核心：没有证据不许标完成。
    /// </summary>
    [TestMethod]
    public void Complete_without_evidence_is_rejected()
    {
        var ex = Assert.ThrowsExactly<GoalTransitionException>(
            () => GoalLifecycle.Complete(Created(), []));

        Assert.AreEqual(GoalLifecycle.CompletionEvidenceRequired, ex.Code);
    }

    [TestMethod]
    public void Complete_rejects_unverifiable_evidence()
    {
        var blank = new[]
        {
            new GoalEvidence(GoalEvidenceKind.Command, "   ", "跑过了"),
        };
        var noSummary = new[]
        {
            new GoalEvidence(GoalEvidenceKind.File, "artifacts/report.md", ""),
        };

        Assert.AreEqual(GoalLifecycle.CompletionEvidenceRequired,
            Assert.ThrowsExactly<GoalTransitionException>(() => GoalLifecycle.Complete(Created(), blank)).Code);
        Assert.AreEqual(GoalLifecycle.CompletionEvidenceRequired,
            Assert.ThrowsExactly<GoalTransitionException>(() => GoalLifecycle.Complete(Created(), noSummary)).Code);
    }

    [TestMethod]
    public void Complete_freezes_evidence_on_the_state()
    {
        var done = GoalLifecycle.Complete(Created(), Evidence());

        Assert.AreEqual(GoalPhase.Complete, done.Phase);
        Assert.AreEqual(1, done.Evidence.Count);
        Assert.AreEqual(GoalEvidenceKind.Test, done.Evidence[0].Kind);
    }

    [TestMethod]
    public void Create_rejects_empty_objective_and_non_positive_rounds()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GoalLifecycle.Create("g1", "  ", 8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GoalLifecycle.Create("g1", "x", 0));
    }
}
