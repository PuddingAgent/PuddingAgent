using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubAgentBudgetLifecycleTests
{
    [TestMethod]
    public void EvaluateBeforeRound_InjectsStartAndDescendingRemainingBudgetNoticesOnce()
    {
        var lifecycle = Create(primaryRounds: 600);

        var start = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);
        var atEighty = lifecycle.EvaluateBeforeRound(120, TimeSpan.FromMinutes(1));
        var eighty = lifecycle.EvaluateBeforeRound(121, TimeSpan.FromMinutes(1));
        var eightyAgain = lifecycle.EvaluateBeforeRound(122, TimeSpan.FromMinutes(1));
        var atFifty = lifecycle.EvaluateBeforeRound(300, TimeSpan.FromMinutes(2));
        var fifty = lifecycle.EvaluateBeforeRound(301, TimeSpan.FromMinutes(2));

        CollectionAssert.AreEqual(
            new[] { "start" },
            start.Notices.Select(n => n.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[] { "remaining_80" },
            eighty.Notices.Select(n => n.Kind).ToArray());
        Assert.AreEqual(0, atEighty.Notices.Count);
        Assert.AreEqual(0, eightyAgain.Notices.Count);
        Assert.AreEqual(0, atFifty.Notices.Count);
        CollectionAssert.AreEqual(
            new[] { "remaining_50" },
            fifty.Notices.Select(n => n.Kind).ToArray());
        StringAssert.Contains(eighty.Notices[0].Message, "479/600");
        StringAssert.Contains(fifty.Notices[0].Message, "299/600");
    }

    [TestMethod]
    public void EvaluateBeforeRound_GrantsTwentyCleanupRoundsAfterPrimaryRoundBudget()
    {
        var lifecycle = Create(primaryRounds: 10, graceRounds: 20);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);

        var graceStart = lifecycle.EvaluateBeforeRound(10, TimeSpan.FromMinutes(1));
        var lastAllowed = lifecycle.EvaluateBeforeRound(29, TimeSpan.FromMinutes(2));
        var exhausted = lifecycle.EvaluateBeforeRound(30, TimeSpan.FromMinutes(2));

        Assert.IsTrue(lifecycle.IsInGrace);
        CollectionAssert.Contains(
            graceStart.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
        Assert.AreEqual(20, graceStart.RemainingGraceRounds);
        Assert.IsFalse(lastAllowed.ShouldStop);
        Assert.AreEqual(1, lastAllowed.RemainingGraceRounds);
        Assert.IsTrue(exhausted.ShouldStop);
        Assert.AreEqual(0, exhausted.RemainingGraceRounds);
    }

    [TestMethod]
    public void EvaluateBeforeRound_ReservesCleanupTimeInsideHardDeadline()
    {
        var lifecycle = Create(
            primaryRounds: 600,
            hardElapsed: TimeSpan.FromMinutes(120),
            graceTimeoutSeconds: 30 * 60);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);

        var before = lifecycle.EvaluateBeforeRound(10, TimeSpan.FromMinutes(89));
        var atPrimaryDeadline = lifecycle.EvaluateBeforeRound(11, TimeSpan.FromMinutes(90));

        Assert.AreEqual(TimeSpan.FromMinutes(90), lifecycle.PrimaryMaxElapsed);
        Assert.AreEqual(TimeSpan.FromMinutes(30), lifecycle.GraceElapsed);
        Assert.IsFalse(before.ShouldStop);
        CollectionAssert.Contains(
            atPrimaryDeadline.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
        Assert.AreEqual("time", atPrimaryDeadline.GraceCause);
    }

    [TestMethod]
    public void Constructor_ShortParentDeadlineLeavesAtLeastThreeQuartersForNormalWork()
    {
        var lifecycle = Create(
            primaryRounds: 600,
            hardElapsed: TimeSpan.FromMinutes(10),
            graceTimeoutSeconds: 30 * 60);

        Assert.AreEqual(TimeSpan.FromMinutes(7.5), lifecycle.PrimaryMaxElapsed);
        Assert.AreEqual(TimeSpan.FromMinutes(2.5), lifecycle.GraceElapsed);
    }

    [TestMethod]
    public void EvaluateBeforeRound_ResumeNoticeExplainsCountersResetAndContextPreserved()
    {
        var lifecycle = Create(primaryRounds: 600, resumed: true);

        var decision = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);

        StringAssert.Contains(decision.Notices[0].Message, "保留原子代理会话和上下文");
        StringAssert.Contains(decision.Notices[0].Message, "计数器已重置");
    }

    [TestMethod]
    public void EvaluateBeforeRound_ToolBudgetExhaustionEntersGraceWithToolsCause()
    {
        var lifecycle = Create(primaryRounds: 600, graceRounds: 20, maxToolCalls: 10);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero, toolCallsUsed: 0);

        var before = lifecycle.EvaluateBeforeRound(1, TimeSpan.FromMinutes(1), toolCallsUsed: 9);
        var atCap = lifecycle.EvaluateBeforeRound(2, TimeSpan.FromMinutes(2), toolCallsUsed: 10);

        Assert.IsFalse(before.ShouldStop);
        Assert.IsTrue(lifecycle.IsInGrace);
        CollectionAssert.Contains(
            atCap.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
        Assert.AreEqual("tools", atCap.GraceCause);
        StringAssert.Contains(
            atCap.Notices.Single(n => n.Kind == "grace_started").Message,
            "工具调用次数");
    }

    [TestMethod]
    public void EvaluateBeforeRound_ToolBudgetExhaustionStillGrantsCleanupRoundsThenStops()
    {
        var lifecycle = Create(primaryRounds: 600, graceRounds: 20, maxToolCalls: 10);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero, toolCallsUsed: 0);

        var graceStart = lifecycle.EvaluateBeforeRound(2, TimeSpan.FromMinutes(1), toolCallsUsed: 10);
        var lastAllowed = lifecycle.EvaluateBeforeRound(21, TimeSpan.FromMinutes(2), toolCallsUsed: 10);
        var exhausted = lifecycle.EvaluateBeforeRound(22, TimeSpan.FromMinutes(2), toolCallsUsed: 10);

        Assert.AreEqual("tools", graceStart.GraceCause);
        Assert.IsFalse(lastAllowed.ShouldStop);
        Assert.AreEqual(1, lastAllowed.RemainingGraceRounds);
        Assert.IsTrue(exhausted.ShouldStop);
        Assert.AreEqual(0, exhausted.RemainingGraceRounds);
    }

    [TestMethod]
    public void EvaluateBeforeRound_RoundsTakePrecedenceOverToolCauseWhenBothExhausted()
    {
        var lifecycle = Create(primaryRounds: 10, graceRounds: 20, maxToolCalls: 5);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero, toolCallsUsed: 0);

        var decision = lifecycle.EvaluateBeforeRound(10, TimeSpan.FromMinutes(1), toolCallsUsed: 5);

        Assert.AreEqual("rounds", decision.GraceCause);
        CollectionAssert.Contains(
            decision.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
    }

    [TestMethod]
    public void EvaluateBeforeRound_Round601IsGrantedInsideGraceAfter600NormalRounds()
    {
        // N00/S4：600 正常轮完整可用；grace 是加法，第 601 轮仍被授予（进入收尾
        // grace，剩余 20 轮），600+20 全部耗尽后才停下。grace 从不预扣正常轮。
        var lifecycle = Create(primaryRounds: 600);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);

        var lastNormal = lifecycle.EvaluateBeforeRound(599, TimeSpan.FromMinutes(10));
        var round601 = lifecycle.EvaluateBeforeRound(600, TimeSpan.FromMinutes(10));
        var lastGrace = lifecycle.EvaluateBeforeRound(619, TimeSpan.FromMinutes(20));
        var exhausted = lifecycle.EvaluateBeforeRound(620, TimeSpan.FromMinutes(20));

        Assert.IsFalse(lastNormal.ShouldStop);
        CollectionAssert.DoesNotContain(
            lastNormal.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
        Assert.IsFalse(round601.ShouldStop);
        CollectionAssert.Contains(
            round601.Notices.Select(n => n.Kind).ToArray(),
            "grace_started");
        Assert.AreEqual(20, round601.RemainingGraceRounds);
        Assert.IsFalse(lastGrace.ShouldStop);
        Assert.AreEqual(1, lastGrace.RemainingGraceRounds);
        Assert.IsTrue(exhausted.ShouldStop);
        Assert.AreEqual(0, exhausted.RemainingGraceRounds);
    }

    [TestMethod]
    public void EvaluateBeforeRound_Rounds41_61_201_ProgressWithoutAbortsUnder600Budget()
    {
        // N00：长程预算 600 下，旧截断点（40/60/200）之后的轮次照常推进、不中止。
        var lifecycle = Create(primaryRounds: 600);
        _ = lifecycle.EvaluateBeforeRound(0, TimeSpan.Zero);

        foreach (var round in new[] { 41, 61, 201 })
        {
            var decision = lifecycle.EvaluateBeforeRound(round, TimeSpan.FromMinutes(round));
            Assert.IsFalse(decision.ShouldStop, $"round {round} must not stop");
            CollectionAssert.DoesNotContain(
                decision.Notices.Select(n => n.Kind).ToArray(),
                "grace_started");
        }
    }

    private static SubAgentBudgetLifecycle Create(
        int primaryRounds,
        int graceRounds = 20,
        TimeSpan? hardElapsed = null,
        int graceTimeoutSeconds = 30 * 60,
        int maxToolCalls = 2400,
        bool resumed = false)
        => new(
            primaryRounds,
            graceRounds,
            hardElapsed ?? TimeSpan.FromHours(24),
            graceTimeoutSeconds,
            maxToolCallsTotal: maxToolCalls,
            resumed);
}
