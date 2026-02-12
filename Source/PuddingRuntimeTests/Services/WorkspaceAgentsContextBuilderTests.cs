using PuddingCode.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class WorkspaceAgentsContextBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_FormatsWorkspaceAgentsAndMessagingHint()
    {
        var provider = new RecordingAgentRosterProvider([
            new AgentRosterItem(
                "agent-b",
                "Audit Agent",
                "agent:agent-b",
                "idle",
                true,
                ["template:audit-agent"],
                null),
        ]);
        var builder = new WorkspaceAgentsContextBuilder(provider);

        var context = await builder.BuildAsync("default", "room-default", CancellationToken.None);

        Assert.AreEqual("default", provider.LastWorkspaceId);
        Assert.AreEqual("room-default", provider.LastRoomId);
        StringAssert.Contains(context, "--- LAYER: WORKSPACE AGENTS ---");
        StringAssert.Contains(context, "agent:agent-b");
        StringAssert.Contains(context, "Audit Agent");
        StringAssert.Contains(context, "send_message");
        StringAssert.Contains(context, "list_agents");
    }

    [TestMethod]
    public async Task BuildAsync_ReturnsEmptyLayerWhenProviderUnavailable()
    {
        var builder = new WorkspaceAgentsContextBuilder(null);

        var context = await builder.BuildAsync("default", "room-default", CancellationToken.None);

        StringAssert.Contains(context, "(No workspace agents available.)");
    }

    private sealed class RecordingAgentRosterProvider(IReadOnlyList<AgentRosterItem> agents) : IAgentRosterProvider
    {
        public string? LastWorkspaceId { get; private set; }
        public string? LastRoomId { get; private set; }

        public Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
            string workspaceId,
            string roomId,
            bool includeBusy,
            bool includeFrozen,
            CancellationToken ct)
        {
            LastWorkspaceId = workspaceId;
            LastRoomId = roomId;
            return Task.FromResult(agents);
        }
    }
}
