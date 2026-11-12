using System.Collections.Immutable;
using PuddingAgents;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingAgentsTests;

[TestClass]
public sealed class DelegationOwnershipTests
{
    [TestMethod]
    public async Task SubmitChild_InjectsTheBoundParent_NotAClientString()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var binding = AgentTestFixtures.Binding();
        var receipt = await service.SubmitChildAsync(binding,
            new("child-1", "work-1", "do the child work", ["read"], 100, 1m));
        Assert.AreEqual("work-1", receipt.ParentOperationId);
        var command = host.SubmittedChildren.Single();
        Assert.AreEqual(binding.OwnerWorkId, command.ParentOperationId, "The parent identity comes from the binding.");
        Assert.AreEqual(binding.WorkspaceId, command.WorkspaceId);
        Assert.AreEqual(binding.AgentId, command.AgentId);
    }

    [TestMethod]
    public async Task SubmitChild_CannotForgeAnotherParent_OrExceedParentLimits()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var binding = AgentTestFixtures.Binding(parentOutput: 1000);
        var foreignParent = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.SubmitChildAsync(binding, new("child-1", "work-2", "x", ["read"])));
        Assert.AreEqual(AgentErrorCodes.Forbidden, foreignParent.Code);
        var tooLarge = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.SubmitChildAsync(binding, new("child-2", "work-1", "x", ["read"], 5000)));
        Assert.AreEqual(AgentErrorCodes.ChildLimitExceedsParent, tooLarge.Code);
        var deniedCapability = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.SubmitChildAsync(binding, new("child-3", "work-1", "x", ["write"])));
        Assert.AreEqual(AgentErrorCodes.CapabilityDenied, deniedCapability.Code);
        Assert.AreEqual(0, host.SubmittedChildren.Count);
    }

    [TestMethod]
    public async Task Acquire_IsAtomic_TwoConcurrentCallersOnlyOneWins()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var binding = AgentTestFixtures.Binding();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                await service.AcquireAsync(binding, new($"acquire-{i}", "ws-1", "agent-1", ["read"], 1000));
                return "acquired";
            }
            catch (AgentBackendException ex) { return ex.Code; }
        })));
        Assert.AreEqual(1, outcomes.Count(o => o == "acquired"));
        Assert.AreEqual(5, outcomes.Count(o => o == AgentErrorCodes.LeaseBusy));
    }

    [TestMethod]
    public async Task Acquire_ReplaySameOperation_ReturnsTheSameLease()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var binding = AgentTestFixtures.Binding();
        var first = await service.AcquireAsync(binding, new("acquire-1", "ws-1", "agent-1", ["read"], 1000));
        var replay = await service.AcquireAsync(binding, new("acquire-1", "ws-1", "agent-1", ["read"], 1000));
        Assert.AreEqual(first.LeaseId, replay.LeaseId);
        Assert.AreEqual(first.Generation, replay.Generation);
        Assert.AreEqual(binding.OwnerWorkId, replay.OwnerWorkId);
    }

    [TestMethod]
    public async Task Release_IsFencedByOwnerAndGeneration_AndReacquireAdvancesGeneration()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var binding = AgentTestFixtures.Binding();
        var lease = await service.AcquireAsync(binding, new("acquire-1", "ws-1", "agent-1", ["read"], 1000));

        var foreignOwner = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ReleaseAsync(binding, new(lease.LeaseId, lease.Generation, "work-2")));
        Assert.AreEqual(AgentErrorCodes.Forbidden, foreignOwner.Code);
        var staleGeneration = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ReleaseAsync(binding, new(lease.LeaseId, lease.Generation + 1, binding.OwnerWorkId)));
        Assert.AreEqual(AgentErrorCodes.LeaseReleaseRejected, staleGeneration.Code);

        await service.ReleaseAsync(binding, new(lease.LeaseId, lease.Generation, binding.OwnerWorkId));
        var again = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.ReleaseAsync(binding, new(lease.LeaseId, lease.Generation, binding.OwnerWorkId)));
        Assert.AreEqual(AgentErrorCodes.LeaseReleaseRejected, again.Code, "A duplicate release is rejected, not silently accepted.");

        var reacquired = await service.AcquireAsync(binding, new("acquire-2", "ws-1", "agent-1", ["read"], 1000));
        Assert.IsTrue(reacquired.Generation > lease.Generation, "A reacquired lease gets a new generation.");
    }

    [TestMethod]
    public async Task Acquire_InAnotherWorkspace_IsForbidden()
    {
        var host = new AgentTestHost();
        var service = new AgentDelegationService(host, host);
        var forbidden = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.AcquireAsync(AgentTestFixtures.Binding(), new("acquire-1", "ws-2", "agent-1", ["read"], 1000)));
        Assert.AreEqual(AgentErrorCodes.Forbidden, forbidden.Code);
    }
}
