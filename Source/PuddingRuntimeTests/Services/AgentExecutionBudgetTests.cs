using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionBudgetTests
{
    [TestMethod]
    public void ResolveMaxToolCallsTotal_UsesExplicitRequestValueWithoutGlobalClamp()
    {
        var actual = AgentExecutionService.ResolveMaxToolCallsTotal(200);

        Assert.AreEqual(200, actual);
    }

    [TestMethod]
    public void ResolveMaxToolCallsTotal_NonPositiveValueUsesGuardrailsSystemDefault()
    {
        var actual = AgentExecutionService.ResolveMaxToolCallsTotal(0);

        Assert.AreEqual(400, actual);
    }

    [TestMethod]
    public void ResolveMaxToolCallsTotal_NonPositiveValueHonorsConfiguredGuardrails()
    {
        var guardrails = new AgentExecutionGuardrails { MaxToolCallsTotal = 777 };

        var actual = AgentExecutionService.ResolveMaxToolCallsTotal(0, guardrails);

        Assert.AreEqual(777, actual);
    }

    // ── N00 残留修复：轮次预算解析（ResolveMaxRounds） ──────────────────

    [TestMethod]
    public void ResolveMaxRounds_ExplicitRequestAboveGuardrailsDefaultIsHonoredWithoutClamp()
    {
        // 回归：显式 1200 + 护栏默认 600 时，此前被 Math.Min 静默压回 600。
        var guardrails = new AgentExecutionGuardrails();

        Assert.AreEqual(1200, AgentExecutionService.ResolveMaxRounds(1200, guardrails));
    }

    [TestMethod]
    public void ResolveMaxRounds_ExplicitVeryLargeRequestIsHonoredWithoutClamp()
    {
        Assert.AreEqual(10000, AgentExecutionService.ResolveMaxRounds(10000, new AgentExecutionGuardrails()));
    }

    [TestMethod]
    public void ResolveMaxRounds_SmallExplicitRequestsAreHonored()
    {
        var guardrails = new AgentExecutionGuardrails();

        Assert.AreEqual(8, AgentExecutionService.ResolveMaxRounds(8, guardrails));
        Assert.AreEqual(32, AgentExecutionService.ResolveMaxRounds(32, guardrails));
    }

    [TestMethod]
    public void ResolveMaxRounds_NonPositiveRequestFallsBackToConfiguredGuardrails()
    {
        var guardrails = new AgentExecutionGuardrails { MaxRounds = 900 };

        Assert.AreEqual(900, AgentExecutionService.ResolveMaxRounds(0, guardrails));
        Assert.AreEqual(900, AgentExecutionService.ResolveMaxRounds(-5, guardrails));
    }

    [TestMethod]
    public void ResolveMaxRounds_NonPositiveRequestWithoutGuardrailsFallsBackToContractDefault()
    {
        Assert.AreEqual(
            PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxRounds,
            AgentExecutionService.ResolveMaxRounds(0));
    }

    // ── N00 残留修复：系统 profile 默认单一来源收敛 ────────────────────

    [TestMethod]
    public void Guardrails_DefaultsDeriveFromAuthoritativeContractConstants()
    {
        var guardrails = new AgentExecutionGuardrails();

        Assert.AreEqual(PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxRounds, guardrails.MaxRounds);
        Assert.AreEqual(
            PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
            guardrails.MaxToolCallsTotal);
        Assert.AreEqual(
            TimeSpan.FromSeconds(PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxTimeoutSeconds),
            guardrails.MaxElapsed);
    }

    [TestMethod]
    public void ResolveMaxToolCallsTotal_NonPositiveRequestUsesConvergedGuardrailsDefault()
    {
        // 兼容直连入口（请求显式 0）应回退系统 profile 默认 2400，而非旧值 400。
        Assert.AreEqual(
            PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
            AgentExecutionService.ResolveMaxToolCallsTotal(0, new AgentExecutionGuardrails()));
    }
}
