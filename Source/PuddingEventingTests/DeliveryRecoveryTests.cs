using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.EventingTestHelpers;
using PuddingEventing;
using PuddingEventing.Contracts;

namespace PuddingEventingTests;

/// <summary>PA-07A: job submission is non-blocking and owner-fenced; checkpoint commit is atomic on revision conflict.</summary>
[TestClass]
public sealed class DeliveryRecoveryTests
{
    [TestMethod]
    public async Task JobSubmit_ReturnsATicketWithoutRunningTheJob()
    {
        var (host, coordinator, binding) = New();
        var receipt = await coordinator.SubmitJobAsync(binding,
            new("review.format", "ref://input-1", "sha256-input", 60, ["audit.write"]));
        Assert.IsFalse(string.IsNullOrWhiteSpace(receipt.JobId));
        Assert.AreEqual(1L, receipt.AcceptedSequence);
        var view = await coordinator.GetJobAsync(binding, receipt.JobId);
        Assert.AreEqual("Accepted", view.State, "Submit only accepts; it never waits for the job to run.");
    }

    [TestMethod]
    public async Task JobSubmit_IsIdempotentPerOperation_AndDifferentContentConflicts()
    {
        var (_, coordinator, binding) = New();
        var request = new SubmitPluginJob("review.format", "ref://input-1", "sha256-input", 60, ["audit.write"]);
        var first = await coordinator.SubmitJobAsync(binding, request);
        var replay = await coordinator.SubmitJobAsync(binding, request);
        Assert.AreEqual(first.JobId, replay.JobId);
        Assert.AreEqual(first.AcceptedSequence, replay.AcceptedSequence);

        var conflict = await Assert.ThrowsExactlyAsync<PluginJobException>(() =>
            coordinator.SubmitJobAsync(binding,
                new("review.format", "ref://input-1", "sha256-other", 60, ["audit.write"])));
        Assert.AreEqual(JobErrorCodes.Conflict, conflict.Code);
    }

    [TestMethod]
    public async Task JobGet_AndCancel_AreFencedByGeneration()
    {
        var (_, coordinator, binding) = New();
        var receipt = await coordinator.SubmitJobAsync(binding,
            new("review.format", "ref://input-1", "sha256-input", 60, ["audit.write"]));

        var staleGet = await Assert.ThrowsExactlyAsync<PluginJobException>(() =>
            coordinator.GetJobAsync(binding with { Generation = 2 }, receipt.JobId));
        Assert.AreEqual(JobErrorCodes.GenerationStale, staleGet.Code);

        var staleCancel = await Assert.ThrowsExactlyAsync<PluginJobException>(() =>
            coordinator.CancelJobAsync(binding with { Generation = 2 }, new(receipt.JobId)));
        Assert.AreEqual(JobErrorCodes.GenerationStale, staleCancel.Code);

        var cancelled = await coordinator.CancelJobAsync(binding, new(receipt.JobId));
        Assert.AreEqual("Cancelled", cancelled.State);
        Assert.AreEqual("plugin.cancelled", cancelled.ReasonCode);
    }

    [TestMethod]
    public async Task JobTransition_FromAnOldGeneration_IsRejected()
    {
        var (host, coordinator, binding) = New();
        var receipt = await coordinator.SubmitJobAsync(binding,
            new("review.format", "ref://input-1", "sha256-input", 60, ["audit.write"]));
        var stale = await Assert.ThrowsExactlyAsync<JobException>(async () =>
        {
            await host.TransitionAsync(receipt.JobId, pluginGeneration: 2, PluginJobState.Succeeded, null, "ref://out", "sha256-out");
        });
        Assert.AreEqual(JobErrorCodes.GenerationStale, stale.Code);

        var succeeded = await host.TransitionAsync(receipt.JobId, pluginGeneration: 1, PluginJobState.Succeeded,
            null, "ref://out", "sha256-out");
        Assert.AreEqual(PluginJobState.Succeeded, succeeded.State);
        Assert.AreEqual("sha256-out", succeeded.ResultHash);
        var again = await Assert.ThrowsExactlyAsync<JobException>(async () =>
        {
            await host.TransitionAsync(receipt.JobId, 1, PluginJobState.Failed, null, null, null);
        });
        Assert.AreEqual(JobErrorCodes.StateInvalid, again.Code);
    }

    [TestMethod]
    public async Task JobRequiredGrant_NotGranted_IsRejected()
    {
        var (_, coordinator, binding) = New();
        var missing = await Assert.ThrowsExactlyAsync<PluginJobException>(() =>
            coordinator.SubmitJobAsync(binding,
                new("review.format", "ref://input-1", "sha256-input", 60, ["goal.write"])));
        Assert.AreEqual(JobErrorCodes.GrantMissing, missing.Code);
    }

    [TestMethod]
    public async Task JobBinding_WithoutHostOwnership_IsRejected()
    {
        var (_, coordinator, binding) = New();
        var invalid = binding with { RootBudgetId = "" };
        var failure = await Assert.ThrowsExactlyAsync<PluginJobException>(() =>
            coordinator.SubmitJobAsync(invalid, new("review.format", "ref://input-1", "sha256-input", 60, ["audit.write"])));
        Assert.AreEqual(JobErrorCodes.BindingInvalid, failure.Code);
    }

    [TestMethod]
    public async Task Checkpoint_StaleRevision_RejectsTheWholeBatch()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        var pending = await host.ListPendingAsync(sub.SubscriptionId);
        host.AdvanceCursorForTest(sub.SubscriptionId, 1);

        var conflict = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
        {
            await host.AckAndAdvanceAsync(sub.SubscriptionId, pending[0].LeaseOwner!, [pending[0].DeliveryId], 1, expectedRevision: 0);
        });
        Assert.AreEqual(EventingErrorCodes.CursorConflict, conflict.Code);
        Assert.AreEqual(1, host.PendingCount(sub.SubscriptionId), "A rejected batch removes no delivery.");
        Assert.AreEqual(1L, (await host.GetAsync(sub.SubscriptionId)).CursorSequence, "The concurrent writer's cursor is kept.");
        Assert.AreEqual(1, page.Deliveries.Length);
    }

    [TestMethod]
    public async Task Checkpoint_AfterCancellation_IsRejected()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        await coordinator.UnsubscribeAsync(binding, new(sub.SubscriptionId));
        var rejected = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
        {
            await host.AckAndAdvanceAsync(sub.SubscriptionId, "owner-x", ["missing"], 1, expectedRevision: 0);
        });
        Assert.AreEqual(EventingErrorCodes.SubscriptionCancelled, rejected.Code);
    }

    private static (InMemoryEventingHost Host, DeliveryCoordinator Coordinator, HostPluginBinding Binding) New()
    {
        var host = new InMemoryEventingHost();
        var dispatcher = new SubscriptionDispatcher(host, host, host, host);
        var coordinator = new DeliveryCoordinator(host, dispatcher, host, host);
        return (host, coordinator, EventingTestFixtures.Binding());
    }
}
