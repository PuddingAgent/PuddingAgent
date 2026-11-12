using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class AgentFacadeClientTests
{
    [TestMethod]
    public async Task DirectoryClient_ForwardsBorrowedHandleQueries_AndReturnsNullForAbsence()
    {
        var api = new FakeDirectory();
        var client = new AgentDirectoryClient(api);
        var page = await client.ListAsync(new("ws-1", 10, null));
        Assert.AreEqual("agent-1", page.Agents[0].AgentId);
        Assert.AreEqual("ws-1", api.WorkspaceId);
        Assert.IsNull(await client.GetAsync(new("ws-1", "agent-missing")));
    }

    [TestMethod]
    public async Task DelegationClient_ForwardsChildLeaseAndRelease_WithoutInventingIdentity()
    {
        var api = new FakeDelegation();
        var client = new AgentDelegationClient(api);
        var receipt = await client.SubmitChildAsync(new("child-1", "work-1", "text", ["read"]));
        Assert.AreEqual("child-1", receipt.ChildOperationId);
        var lease = await client.AcquireAsync(new("acquire-1", "ws-1", "agent-1", ["read"], 1000));
        Assert.AreEqual("lease-1", lease.LeaseId);
        await client.ReleaseAsync(new("lease-1", 1, "work-1"));
        Assert.AreEqual("lease-1", api.Released);
    }

    private sealed class FakeDirectory : IAgentDirectoryApi
    {
        public string? WorkspaceId;

        public Task<AgentDirectoryPage> ListAsync(AgentListQuery query, CancellationToken ct = default)
        {
            WorkspaceId = query.WorkspaceId;
            return Task.FromResult(new AgentDirectoryPage(
                [new("ws-1", "agent-1", "Agent 1", "template-1", 1, 2, "Active", DateTimeOffset.UtcNow)],
                null, true));
        }

        public Task<AgentDirectoryEntry?> GetAsync(AgentRef agent, CancellationToken ct = default)
            => Task.FromResult<AgentDirectoryEntry?>(null);
    }

    private sealed class FakeDelegation : IAgentDelegationApi
    {
        public string? Released;

        public Task<DelegationReceipt> SubmitChildAsync(DelegateWorkRequest request, CancellationToken ct = default)
            => Task.FromResult(new DelegationReceipt(request.OperationId, "child-1", "work-1", "Accepted",
                DateTimeOffset.UtcNow));

        public Task<SubAgentLease> AcquireAsync(AcquireSubAgentRequest request, CancellationToken ct = default)
            => Task.FromResult(new SubAgentLease("lease-1", request.WorkspaceId, request.AgentId, 1, "work-1",
                DateTimeOffset.UtcNow.AddMinutes(1), "Active"));

        public Task ReleaseAsync(ReleaseSubAgentRequest request, CancellationToken ct = default)
        {
            Released = request.LeaseId;
            return Task.CompletedTask;
        }
    }
}
