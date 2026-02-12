using PuddingCode.Abstractions;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class WorkspaceAgentRosterProviderTests
{
    [TestMethod]
    public async Task ListAgentsAsync_MapsEnabledUnfrozenWorkspaceAgents()
    {
        var catalog = new RecordingWorkspaceAgentCatalog([
            CreateAgent("agent-a", "General Assistant", "general-assistant", isEnabled: true, isFrozen: false),
            CreateAgent("agent-frozen", "Frozen Agent", "audit-agent", isEnabled: true, isFrozen: true),
            CreateAgent("agent-disabled", "Disabled Agent", "task-agent", isEnabled: false, isFrozen: false),
        ]);
        var provider = new WorkspaceAgentRosterProvider(catalog);

        var agents = await provider.ListAgentsAsync(
            "default",
            "room-default",
            includeBusy: true,
            includeFrozen: false,
            CancellationToken.None);

        Assert.AreEqual("default", catalog.LastWorkspaceId);
        Assert.HasCount(1, agents);

        var agent = agents[0];
        Assert.AreEqual("agent-a", agent.AgentId);
        Assert.AreEqual("General Assistant", agent.DisplayName);
        Assert.AreEqual("agent:agent-a", agent.Address);
        Assert.AreEqual("idle", agent.Status);
        Assert.IsTrue(agent.CanReceiveMessages);
        CollectionAssert.Contains(agent.Capabilities.ToArray(), "template:general-assistant");
    }

    [TestMethod]
    public async Task ListAgentsAsync_CanIncludeFrozenAgents()
    {
        var catalog = new RecordingWorkspaceAgentCatalog([
            CreateAgent("agent-frozen", "Frozen Agent", "audit-agent", isEnabled: true, isFrozen: true),
        ]);
        var provider = new WorkspaceAgentRosterProvider(catalog);

        var agents = await provider.ListAgentsAsync(
            "default",
            "room-default",
            includeBusy: true,
            includeFrozen: true,
            CancellationToken.None);

        Assert.HasCount(1, agents);
        Assert.AreEqual("frozen", agents[0].Status);
        Assert.IsFalse(agents[0].CanReceiveMessages);
    }

    private static WorkspaceAgentDto CreateAgent(
        string agentId,
        string name,
        string sourceTemplateId,
        bool isEnabled,
        bool isFrozen) =>
        new(
            AgentId: agentId,
            Name: name,
            Description: null,
            DisplayName: name,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: sourceTemplateId,
            MainSessionId: null,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: isEnabled,
            IsFrozen: isFrozen,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingWorkspaceAgentCatalog(IReadOnlyList<WorkspaceAgentDto> agents) : IWorkspaceAgentCatalog
    {
        public string? LastWorkspaceId { get; private set; }

        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
        {
            LastWorkspaceId = workspaceId;
            return Task.FromResult(agents);
        }
    }
}
