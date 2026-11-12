using PuddingAgents;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;
using CancelWorkEffect = Pudding.Api.Contracts.CancelWorkEffect;

namespace PuddingAgentsTests;

[TestClass]
public sealed class AgentWorkControlTests
{
    [TestMethod]
    public async Task Observe_ReturnsBoundEventsAndTerminalStatus()
    {
        var host = new AgentTestHost();
        host.AddWork("ws-1", "op-1", "Completed",
            new WorkEventRecord(1, "accepted", DateTimeOffset.UtcNow, null, null, null),
            new WorkEventRecord(2, "completed", DateTimeOffset.UtcNow, null, "ref://result", "sha256-result"));
        var service = new AgentWorkControlService(host, host, host);
        var observation = await service.ObserveAsync(AgentTestFixtures.Binding(),
            new(new("ws-1", "agent-1"), "op-1", 0));
        Assert.AreEqual("Completed", observation.Status);
        Assert.AreEqual(2, observation.Events.Length);
        Assert.AreEqual("ref://result", observation.Events[1].PayloadRef);
        Assert.AreEqual(2L, observation.NextSequence);
        Assert.IsTrue(observation.Complete);
        Assert.IsFalse(observation.SnapshotRequired);
    }

    [TestMethod]
    public async Task Observe_ReturnsSnapshotRequired_WhenTheCursorPredatesRetention()
    {
        var host = new AgentTestHost();
        host.AddWork("ws-1", "op-1", "Running", new WorkEventRecord(3, "running", DateTimeOffset.UtcNow, null, null, null));
        host.SetRetention("ws-1", "op-1", 3);
        var service = new AgentWorkControlService(host, host, host);
        var observation = await service.ObserveAsync(AgentTestFixtures.Binding(), new(new("ws-1", "agent-1"), "op-1", 0));
        Assert.IsTrue(observation.SnapshotRequired);
        Assert.AreEqual(3L, observation.OldestAvailableSequence);
        Assert.AreEqual(0, observation.Events.Length);
    }

    [TestMethod]
    public async Task Cancel_IsRequestedOnce_AndReplayReturnsTheOriginalReceipt()
    {
        var host = new AgentTestHost();
        var service = new AgentWorkControlService(host, host, host);
        var binding = AgentTestFixtures.Binding();
        var request = new CancelWorkRequest("cancel-1", new("ws-1", "agent-1"), "op-1", 1, "user.cancelled");

        var first = await service.CancelAsync(binding, request);
        var replay = await service.CancelAsync(binding, request);
        Assert.AreEqual(CancelWorkEffect.Requested, first.Effect);
        Assert.AreEqual(first, replay, "A replayed cancel returns the original receipt.");
        Assert.AreEqual(1, host.CancelCalls, "The gateway must be called exactly once for the same operation id.");
    }

    [TestMethod]
    public async Task Cancel_SameOperationWithDifferentContent_Conflicts()
    {
        var host = new AgentTestHost();
        var service = new AgentWorkControlService(host, host, host);
        var binding = AgentTestFixtures.Binding();
        await service.CancelAsync(binding, new("cancel-1", new("ws-1", "agent-1"), "op-1", 1));
        var conflict = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.CancelAsync(binding, new("cancel-1", new("ws-1", "agent-1"), "op-2", 1)));
        Assert.AreEqual(AgentErrorCodes.OperationConflict, conflict.Code);
        Assert.AreEqual(1, host.CancelCalls);
    }

    [TestMethod]
    public async Task Cancel_PassesThroughAlreadyTerminal_WithoutClaimingTermination()
    {
        var host = new AgentTestHost
        {
            ForcedCancelOutcome = new WorkCancelOutcome(PuddingAgents.Contracts.CancelWorkEffect.AlreadyTerminal, 7, "Completed"),
        };
        var service = new AgentWorkControlService(host, host, host);
        var receipt = await service.CancelAsync(AgentTestFixtures.Binding(),
            new("cancel-1", new("ws-1", "agent-1"), "op-1", 7));
        Assert.AreEqual(CancelWorkEffect.AlreadyTerminal, receipt.Effect);
        Assert.AreEqual("Completed", receipt.CurrentStatus);
        Assert.AreEqual(7L, receipt.Revision);
    }

    [TestMethod]
    public async Task Cancel_InAnotherWorkspace_IsForbiddenBeforeTheGateway()
    {
        var host = new AgentTestHost();
        var service = new AgentWorkControlService(host, host, host);
        var forbidden = await Assert.ThrowsExactlyAsync<AgentBackendException>(async () =>
            await service.CancelAsync(AgentTestFixtures.Binding(), new("cancel-1", new("ws-2", "agent-1"), "op-1", 1)));
        Assert.AreEqual(AgentErrorCodes.Forbidden, forbidden.Code);
        Assert.AreEqual(0, host.CancelCalls);
    }
}
