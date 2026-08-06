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
}
