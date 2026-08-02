using PuddingRuntime.Services;

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
    public void ResolveMaxToolCallsTotal_NonPositiveValueUsesRequestContractDefault()
    {
        var actual = AgentExecutionService.ResolveMaxToolCallsTotal(0);

        Assert.AreEqual(100, actual);
    }
}
