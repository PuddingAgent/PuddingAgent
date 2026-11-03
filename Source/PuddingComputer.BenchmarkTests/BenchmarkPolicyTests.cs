using PuddingComputer.Benchmark;

namespace PuddingComputer.BenchmarkTests;

/// <summary>
/// <see cref="BenchmarkPolicy.Decide"/> 的判定表。
/// <para>
/// 这是整个 benchmark 组件唯一有分支的地方，锁住的是 Codex 两轮规划里最硬的两条：
/// </para>
/// <list type="number">
/// <item>只有 <c>Pass</c> 允许声明成功 —— <c>Unknown</c> 重试多少次都不会变成功。</item>
/// <item>非幂等/破坏性动作在 <c>Unknown</c> 下不得盲重试。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class BenchmarkPolicyTests
{
    private static BenchmarkTask Task(RiskClass risk, int maxAttempts = 3) =>
        new("t1", "calculator", "launch-clean", "Compute 17 + 25", risk, maxAttempts,
            new UiaPropertyOracle("CalculatorResults", "name", "endsWith", "42"));

    private static OracleEvaluation Pass() =>
        OracleEvaluation.Pass("name = \"42\"", "uia[CalculatorResults].name");

    private static OracleEvaluation Fail() =>
        OracleEvaluation.Fail("name = \"17\"", "uia[CalculatorResults].name");

    private static OracleEvaluation Unknown() =>
        OracleEvaluation.Unknown("未找到 CalculatorResults 节点", "uia[CalculatorResults].name");

    /// <summary>通过 → 声明成功。</summary>
    [TestMethod]
    public void Pass_YieldsSucceeded()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.Reversible), Pass(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Succeeded, decision);
    }

    /// <summary>
    /// 即使次数已用尽，本次通过仍应报成功。
    /// <para>
    /// 否则"最后一次才成功"会被错误地报成 blocked —— 判定顺序必须先看成功。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Pass_OnLastAllowedAttempt_StillSucceeds()
    {
        var task = Task(RiskClass.Reversible, maxAttempts: 3);

        var decision = BenchmarkPolicy.Decide(task, Pass(), attemptsUsed: 3);

        Assert.AreEqual(AttemptDecision.Succeeded, decision);
    }

    /// <summary>失败但还有余量 → 再试。</summary>
    [TestMethod]
    public void Fail_WithRemainingAttempts_Retries()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.Reversible), Fail(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Retry, decision);
    }

    /// <summary>失败且次数用尽 → 阻塞。</summary>
    [TestMethod]
    public void Fail_WithAttemptsExhausted_Blocks()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.Reversible), Fail(), attemptsUsed: 3);

        Assert.AreEqual(AttemptDecision.Blocked, decision);
    }

    /// <summary>
    /// **核心用例**：非幂等动作 + 不确定 → 不得重试。
    /// <para>
    /// "上一步可能已经生效"正是这类动作最危险的失败形态；
    /// 再试一次可能造成第二个副作用（发出两条消息）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Unknown_OnNonIdempotentAction_Blocks()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.NonIdempotent), Unknown(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Blocked, decision,
            "非幂等动作在结果不确定时不得盲重试");
    }

    /// <summary>**核心用例**：破坏性动作 + 不确定 → 不得重试。</summary>
    [TestMethod]
    public void Unknown_OnDestructiveAction_Blocks()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.DestructiveOrSensitive), Unknown(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Blocked, decision,
            "破坏性动作在结果不确定时不得盲重试");
    }

    /// <summary>可逆动作 + 不确定 → 允许再试（试错成本低）。</summary>
    [TestMethod]
    public void Unknown_OnReversibleAction_Retries()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.Reversible), Unknown(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Retry, decision);
    }

    /// <summary>只读动作 + 不确定 → 允许再试。</summary>
    [TestMethod]
    public void Unknown_OnReadOnlyAction_Retries()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.ReadOnly), Unknown(), attemptsUsed: 1);

        Assert.AreEqual(AttemptDecision.Retry, decision);
    }

    /// <summary>
    /// 次数上限优先于风险豁免：可逆动作次数用尽后也必须停。
    /// </summary>
    [TestMethod]
    public void Unknown_WithAttemptsExhausted_Blocks()
    {
        var decision = BenchmarkPolicy.Decide(Task(RiskClass.Reversible), Unknown(), attemptsUsed: 3);

        Assert.AreEqual(AttemptDecision.Blocked, decision);
    }

    /// <summary>
    /// 风险分级本身要能取红：四个等级的"是否禁止不确定重试"必须两两区分清楚，
    /// 合并任意两个都会让上面的用例失去意义。
    /// </summary>
    [TestMethod]
    public void RiskClass_ForbidsRetryOnUnknown_MatchesTable()
    {
        Assert.IsFalse(RiskClass.ReadOnly.ForbidsRetryOnUnknown());
        Assert.IsFalse(RiskClass.Reversible.ForbidsRetryOnUnknown());
        Assert.IsTrue(RiskClass.NonIdempotent.ForbidsRetryOnUnknown());
        Assert.IsTrue(RiskClass.DestructiveOrSensitive.ForbidsRetryOnUnknown());
    }

    /// <summary>只有破坏性/敏感动作需要在执行前澄清。</summary>
    [TestMethod]
    public void RiskClass_RequiresClarification_OnlyForDestructive()
    {
        Assert.IsFalse(RiskClass.ReadOnly.RequiresClarificationBeforeActing());
        Assert.IsFalse(RiskClass.Reversible.RequiresClarificationBeforeActing());
        Assert.IsFalse(RiskClass.NonIdempotent.RequiresClarificationBeforeActing());
        Assert.IsTrue(RiskClass.DestructiveOrSensitive.RequiresClarificationBeforeActing());
    }
}
