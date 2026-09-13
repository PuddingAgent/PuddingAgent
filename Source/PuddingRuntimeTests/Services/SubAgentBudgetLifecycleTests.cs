using PuddingCode.Runtime;
using PuddingRuntime.Services;
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

    // === 原子片2：派生预算可执行性统一判据（SubAgentTool 委派边界与批量除法共用） ===

    [TestMethod]
    public void DescribeBudgetInfeasibility_RejectsExhaustedAndBelowFloorAxesWithNumbers()
    {
        // 派生预算不可执行时必须在边界给出带数值的可诊断拒绝，而不是派发首轮即死的子代理。
        var options = new SubAgentExecutionOptions(); // 默认下限 20000 / 1000 / 0.01

        var infeasible = new ExecutionUsageBudget
        {
            MaxInputTokens = 10_323,  // sub-feca2176 实测：父级剩余 10323 < 子代理首轮 24473
            MaxOutputTokens = 0,      // 已耗尽
            MaxCost = 0.001m,         // 低于 0.01 下限
            IsDerivedRemainder = true,
            PeakRoundInputTokens = 24_473,
        };

        var error = options.DescribeBudgetInfeasibility(infeasible);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "sub_agent_parent_budget_infeasible:");
        StringAssert.Contains(error, "input 剩余 10323 < 最小可执行 20000");
        StringAssert.Contains(error, "output 已耗尽（剩余 0）");
        StringAssert.Contains(error, "cost 剩余 0.001 < 最小可执行 0.01");
        StringAssert.Contains(error, "父级单轮峰值 24473");
    }

    [TestMethod]
    public void DescribeBudgetInfeasibility_AllowsViableDerivedBudget()
    {
        var options = new SubAgentExecutionOptions();
        var viable = new ExecutionUsageBudget
        {
            MaxInputTokens = 1_000_000,
            MaxOutputTokens = 100_000,
            MaxCost = 1m,
            IsDerivedRemainder = true,
            PeakRoundInputTokens = 24_473,
        };

        Assert.IsNull(options.DescribeBudgetInfeasibility(viable));
    }

    [TestMethod]
    public void DescribeBudgetInfeasibility_NonDerivedBudgetIsNotGuarded()
    {
        // 回归保护：未派生预算（如 WorkUnit 冻结预算原样透传）的 0 表示「未设置上限」，不受守卫影响。
        var options = new SubAgentExecutionOptions();
        var nonDerived = new ExecutionUsageBudget
        {
            MaxInputTokens = 0,
            MaxOutputTokens = 0,
            MaxCost = 0m,
            IsDerivedRemainder = false,
        };

        Assert.IsNull(options.DescribeBudgetInfeasibility(nonDerived));
    }

    [TestMethod]
    public void DivideUsageBudget_DividesHonestlyAndMarksDerived()
    {
        // 批量除法必须诚实——不再 Math.Max(1, …) 夹出「出生即死」的最小值；
        // 除后份额打 IsDerivedRemainder 标并继承父级单轮峰值，供可执行性判据拒绝。
        var budget = new ExecutionUsageBudget
        {
            MaxInputTokens = 30_000,
            MaxOutputTokens = 2,
            MaxCost = 0.009m,
            PricingKnown = true,
            PeakRoundInputTokens = 24_473,
        };

        var divided = SubAgentInvocationService.DivideUsageBudget(budget, divisor: 3);

        Assert.IsNotNull(divided);
        Assert.AreEqual(10_000L, divided.MaxInputTokens);
        Assert.AreEqual(0L, divided.MaxOutputTokens); // 诚实归零，旧实现会夹成 1
        Assert.AreEqual(0.003m, divided.MaxCost);
        Assert.IsTrue(divided.IsDerivedRemainder);
        Assert.AreEqual(24_473L, divided.PeakRoundInputTokens);

        // 低于下限的份额必须被判据拒绝（批量前缀 + 减少 tasks 建议）
        var options = new SubAgentExecutionOptions();
        var error = options.DescribeBudgetInfeasibility(
            divided,
            errorPrefix: "sub_agent_batch_budget_infeasible:",
            advice: "请减少批量任务数（tasks）或收敛父级轮次后再委派。");
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "sub_agent_batch_budget_infeasible:");
        StringAssert.Contains(error, "output 已耗尽（剩余 0）");
    }

    [TestMethod]
    public void DivideUsageBudget_SingleTaskKeepsBudgetUntouched()
    {
        // divisor<=1 原样返回：非派生预算保持 IsDerivedRemainder=false，不受守卫影响（回归保护）。
        var budget = new ExecutionUsageBudget { MaxInputTokens = 500, IsDerivedRemainder = false };
        var divided = SubAgentInvocationService.DivideUsageBudget(budget, divisor: 1);
        Assert.IsNotNull(divided);
        Assert.AreSame(budget, divided);
        Assert.IsFalse(divided.IsDerivedRemainder);
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
