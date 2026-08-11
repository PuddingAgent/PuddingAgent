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

    private static SubAgentBudgetLifecycle Create(
        int primaryRounds,
        int graceRounds = 20,
        TimeSpan? hardElapsed = null,
        int graceTimeoutSeconds = 30 * 60,
        bool resumed = false)
        => new(
            primaryRounds,
            graceRounds,
            hardElapsed ?? TimeSpan.FromHours(24),
            graceTimeoutSeconds,
            maxToolCallsTotal: 2400,
            resumed);
}
