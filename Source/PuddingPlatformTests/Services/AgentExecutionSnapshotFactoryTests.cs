using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingPlatform.Services.Snapshot;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentExecutionSnapshotFactoryTests
{
    [TestMethod]
    public async Task CreateAsync_ShouldSnapshotAgentExecutionGuardrails()
    {
        var factory = new AgentExecutionSnapshotFactory(
            NullLogger<AgentExecutionSnapshotFactory>.Instance);
        var profile = new AgentRuntimeProfile
        {
            WorkspaceId = "default",
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            MaxRounds = 12,
            MaxElapsedSeconds = 90,
            MaxToolCallsTotal = 7,
        };

        var snapshot = await factory.CreateAsync(
            profile,
            previousSnapshot: null,
            CancellationToken.None);

        Assert.AreEqual(12, snapshot.BudgetMaxRounds);
        Assert.AreEqual(7, snapshot.BudgetMaxToolCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(90), snapshot.Timeout);
    }
}
