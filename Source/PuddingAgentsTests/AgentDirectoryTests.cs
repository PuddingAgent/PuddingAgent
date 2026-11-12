using PuddingAgents;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingAgentsTests;

internal static class AgentTestFixtures
{
    public static AgentBinding Binding(string workspace = "ws-1", long? parentOutput = 1000,
        decimal? parentCost = 10m, params string[] capabilities)
        => new(workspace, "agent-1", "actor-1", "work-1", "budget-1", parentOutput, parentCost,
            capabilities.Length == 0 ? ["read"] : [.. capabilities]);

    public static AgentDirectoryRecord Agent(string workspace = "ws-1", string agentId = "agent-1",
        bool archived = false, long revision = 1)
        => new(workspace, agentId, $"Display {agentId}", "template-1", 3, revision, archived, DateTimeOffset.UtcNow);
}

[TestClass]
public sealed class AgentDirectoryTests
{
    [TestMethod]
    public async Task List_IsScopedToTheBoundWorkspace_AndGetRejectsForeignWorkspace()
    {
        var host = new AgentTestHost();
        host.AddAgent(AgentTestFixtures.Agent("ws-1", "agent-a"));
        host.AddAgent(AgentTestFixtures.Agent("ws-2", "agent-a"));
        var service = new AgentDirectoryService(host);
        var binding = AgentTestFixtures.Binding();

        var page = await service.ListAsync(binding, new("ws-1", 10));
        Assert.AreEqual(1, page.Agents.Length);
        Assert.AreEqual("ws-1", page.Agents[0].WorkspaceId);
        var forbidden = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.GetAsync(binding, new("ws-2", "agent-a")));
        Assert.AreEqual(AgentErrorCodes.Forbidden, forbidden.Code);
    }

    [TestMethod]
    public async Task List_ReportsCursorAndComplete_WithoutClaimingAbsence()
    {
        var host = new AgentTestHost();
        host.AddAgent(AgentTestFixtures.Agent(agentId: "agent-a"));
        host.AddAgent(AgentTestFixtures.Agent(agentId: "agent-b"));
        host.AddAgent(AgentTestFixtures.Agent(agentId: "agent-c"));
        var service = new AgentDirectoryService(host);
        var binding = AgentTestFixtures.Binding();

        var first = await service.ListAsync(binding, new("ws-1", 2));
        Assert.AreEqual(2, first.Agents.Length);
        Assert.IsFalse(first.Complete);
        Assert.AreEqual("agent-b", first.Cursor);

        var second = await service.ListAsync(binding, new("ws-1", 2, first.Cursor));
        Assert.AreEqual(1, second.Agents.Length);
        Assert.IsTrue(second.Complete);
        Assert.IsNull(second.Cursor);
    }

    [TestMethod]
    public async Task TruncatedPage_IsNotAnAbsenceProof_AndGetDistinguishes()
    {
        var host = new AgentTestHost();
        host.AddAgent(AgentTestFixtures.Agent(agentId: "agent-a"));
        host.AddAgent(AgentTestFixtures.Agent(agentId: "agent-b"));
        var service = new AgentDirectoryService(host);
        var binding = AgentTestFixtures.Binding();
        var page = await service.ListAsync(binding, new("ws-1", 1));
        Assert.IsFalse(page.Complete, "A truncated page must not be treated as a complete result.");
        Assert.IsNull(await service.GetAsync(binding, new("ws-1", "agent-z")), "Absence is decided by Get, not by a truncated page.");
    }

    [TestMethod]
    public async Task InvalidPageArguments_AreTyped_AndArchivedStateIsExplicit()
    {
        var host = new AgentTestHost();
        host.AddAgent(AgentTestFixtures.Agent(archived: true));
        var service = new AgentDirectoryService(host);
        var binding = AgentTestFixtures.Binding();
        var size = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ListAsync(binding, new("ws-1", 101)));
        Assert.AreEqual(AgentErrorCodes.RequestInvalid, size.Code);
        var cursor = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ListAsync(binding, new("ws-1", 10, new string('c', 129))));
        Assert.AreEqual(AgentErrorCodes.RequestInvalid, cursor.Code);
        var entry = await service.GetAsync(binding, new("ws-1", "agent-1"));
        Assert.AreEqual("Archived", entry!.State);
    }
}
