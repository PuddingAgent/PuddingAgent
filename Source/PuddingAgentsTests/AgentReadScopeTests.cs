using System.Collections.Immutable;
using PuddingAgents;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingAgentsTests;

/// <summary>
/// PA-02 I0: directory reads use AgentReadBinding (workspace + host actor) and must not require Work/RootBudget.
/// Cross-workspace reads and invalid bindings are rejected before the store is touched.
/// </summary>
[TestClass]
public sealed class AgentReadScopeTests
{
    [TestMethod]
    public async Task DirectoryRead_DoesNotRequireWorkOrBudget()
    {
        var store = new FakeStore();
        var service = new AgentDirectoryService(store);
        var binding = new AgentReadBinding("ws-1", "actor-1");
        var page = await service.ListAsync(binding, new AgentListQuery("ws-1"));
        Assert.AreEqual(1, page.Agents.Length);
        Assert.AreEqual("agent-1", page.Agents[0].AgentId);
        Assert.AreEqual(1, store.ListCalls);
        Assert.AreEqual("agent-1", (await service.GetAsync(binding, new AgentRef("ws-1", "agent-1")))!.AgentId);
    }

    [TestMethod]
    public async Task DirectoryRead_RejectsAnotherWorkspaceOrInvalidBinding()
    {
        var store = new FakeStore();
        var service = new AgentDirectoryService(store);
        var cross = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ListAsync(new AgentReadBinding("ws-1", "actor-1"), new AgentListQuery("ws-2")));
        Assert.AreEqual(AgentErrorCodes.Forbidden, cross.Code);
        Assert.AreEqual(0, store.ListCalls, "A cross-workspace read must not touch the store.");

        var invalid = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ListAsync(new AgentReadBinding("ws-1", ""), new AgentListQuery("ws-1")));
        Assert.AreEqual(AgentErrorCodes.BindingInvalid, invalid.Code);
    }

    private sealed class FakeStore : IAgentDirectoryStore
    {
        public int ListCalls { get; private set; }

        public ValueTask<AgentDirectoryQueryResult> ListAsync(string workspaceId, string? cursor, int maxCount,
            CancellationToken ct = default)
        {
            ListCalls++;
            ImmutableArray<AgentDirectoryRecord> agents =
            [
                new(workspaceId, "agent-1", "Agent", "template", 1, 1, false, DateTimeOffset.UtcNow),
            ];
            return ValueTask.FromResult(new AgentDirectoryQueryResult(agents, null, true));
        }

        public ValueTask<AgentDirectoryRecord?> GetAsync(string workspaceId, string agentId,
            CancellationToken ct = default)
            => ValueTask.FromResult<AgentDirectoryRecord?>(new(workspaceId, agentId, "Agent", "template", 1, 1, false,
                DateTimeOffset.UtcNow));
    }
}
