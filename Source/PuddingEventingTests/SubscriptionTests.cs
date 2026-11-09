using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.EventingTestHelpers;
using PuddingEventing;
using PuddingEventing.Contracts;

namespace PuddingEventingTests;

/// <summary>PA-07A: reliable subscription delivery — authorization filter, at-least-once, contiguous checkpoint,
/// retention snapshot signal, dead letters and transactional outbox visibility.</summary>
[TestClass]
public sealed class SubscriptionTests
{
    [TestMethod]
    public async Task Dispatch_DeliversOnlyAuthorizedKindsInTheBoundWorkspace()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var expected = host.AppendEvent("ws-1", "review.completed", "ws-1");
        host.AppendEvent("ws-1", "review.completed", "ws-2");
        host.AppendEvent("ws-1", "other.kind", "ws-1");

        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(1, page.Deliveries.Length);
        Assert.AreEqual(expected.EventId, page.Deliveries[0].EventId);
        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [expected.EventId]));
        Assert.AreEqual(1, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
        Assert.AreEqual(0, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length);
    }

    [TestMethod]
    public async Task Ack_AdvancesOnlyContiguousPrefix_AndRejectsGaps()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var first = host.AppendEvent("ws-1", "review.completed", "ws-1");
        var second = host.AppendEvent("ws-1", "review.completed", "ws-1");
        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(2, page.Deliveries.Length);

        var failure = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.AckAsync(binding, new(sub.SubscriptionId, [second.EventId])));
        Assert.AreEqual(EventingErrorCodes.CursorGap, failure.Code);
        Assert.AreEqual(0, (await host.GetAsync(sub.SubscriptionId)).CursorSequence, "A gap must not advance the cursor.");
        Assert.AreEqual(2, host.PendingCount(sub.SubscriptionId));

        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [first.EventId, second.EventId]));
        Assert.AreEqual(2, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
    }

    [TestMethod]
    public async Task Ack_CanSkipFilteredOutEventsInsideTheClaimedBatch()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var otherKind = host.AppendEvent("ws-1", "goal.completed", "ws-1");
        var matching = host.AppendEvent("ws-1", "review.completed", "ws-1");
        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(1, page.Deliveries.Length, "Only the granted kind is deliverable.");
        Assert.AreEqual(matching.EventId, page.Deliveries[0].EventId);

        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [matching.EventId]));
        var record = await host.GetAsync(sub.SubscriptionId);
        Assert.AreEqual(matching.Sequence, record.CursorSequence, "The cursor advances over the filtered-out sequence.");
        Assert.AreEqual(0, host.PendingCount(sub.SubscriptionId), "The filtered-out event is not a delivery.");
        Assert.AreNotEqual(otherKind.EventId, page.Deliveries[0].EventId);
    }

    [TestMethod]
    public async Task PageWithNoDeliverableEvents_AdvancesOnlyTheScanPosition()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "goal.completed", "ws-1");
        host.AppendEvent("ws-1", "tool.result", "ws-1");

        var empty = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(0, empty.Deliveries.Length);
        Assert.IsFalse(empty.SnapshotRequired);
        var scanned = await host.GetAsync(sub.SubscriptionId);
        Assert.AreEqual(2L, scanned.ScannedThroughSequence, "The full filtered page is scanned without a fake delivery.");
        Assert.AreEqual(0L, scanned.CursorSequence, "Scan progress is not an acknowledgement.");

        // A second read must not spin on the same page; a later matching event is still delivered.
        Assert.AreEqual(0, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length);
        var matching = host.AppendEvent("ws-1", "review.completed", "ws-1");
        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(1, page.Deliveries.Length);
        Assert.AreEqual(matching.EventId, page.Deliveries[0].EventId);
    }

    [TestMethod]
    public async Task DeadLetter_BlocksCursorUntilExplicitlyReplayed()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var dispatcher = new SubscriptionDispatcher(host, host, host, host);
        var claimed = (await dispatcher.DispatchAsync(sub.SubscriptionId, "owner-a", 10)).Deliveries;
        Assert.AreEqual(2, claimed.Length);

        await dispatcher.FailAsync(sub.SubscriptionId, "owner-a", claimed[0].DeliveryId, "plugin.error", deadLetter: true);
        var blocked = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await dispatcher.AckAsync(sub.SubscriptionId, "owner-a", [claimed[1]], [claimed[1].DeliveryId]));
        Assert.AreEqual(EventingErrorCodes.DeadLetterBlocked, blocked.Code);
        Assert.AreEqual(0, (await host.GetAsync(sub.SubscriptionId)).CursorSequence,
            "A dead letter blocks the checkpoint; it is never silently skipped.");

        var dead = (await dispatcher.DeadLettersAsync(sub.SubscriptionId)).Single();
        await dispatcher.ReplayDeadLetterAsync(sub.SubscriptionId, dead.DeliveryId);
        var reclaimed = await dispatcher.DispatchAsync(sub.SubscriptionId, "owner-a", 10);
        await dispatcher.AckAsync(sub.SubscriptionId, "owner-a", reclaimed.Deliveries,
            [.. reclaimed.Deliveries.Select(d => d.DeliveryId)]);
        Assert.AreEqual(2, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
    }

    [TestMethod]
    public async Task PartialAck_OfTheSameClaimedBatch_KeepsTheRestLeased()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var first = host.AppendEvent("ws-1", "review.completed", "ws-1");
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(2, page.Deliveries.Length);

        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [first.EventId]));
        Assert.AreEqual(first.Sequence, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
        Assert.AreEqual(1, host.PendingCount(sub.SubscriptionId));
        var remaining = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(1, remaining.Deliveries.Length);
        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [remaining.Deliveries[0].EventId]));
        Assert.AreEqual(2, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
    }

    [TestMethod]
    public async Task RetentionBoundary_UsesAfterPlusOneBeforeOldest()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.SetOldestAvailable("ws-1", 10);
        host.SetCheckpointForTest(sub.SubscriptionId, 9, 9);
        var fresh = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.IsFalse(fresh.SnapshotRequired, "After=9 with oldest=10 is still readable.");
        host.SetCheckpointForTest(sub.SubscriptionId, 8, 8);
        var expired = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.IsTrue(expired.SnapshotRequired, "After=8 with oldest=10 requires a snapshot.");
        Assert.AreEqual(10L, expired.OldestAvailableSequence);
    }
    [TestMethod]
    public async Task CrashBeforeAck_RedeliversTheSameLogicalEvent()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var firstRead = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(1, firstRead.Deliveries.Length);

        // Simulated crash between processing and acknowledgement: the cursor never moved and the lease is re-issued.
        var secondRead = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.AreEqual(firstRead.Deliveries[0].EventId, secondRead.Deliveries[0].EventId);
        Assert.AreEqual(1, host.PendingCount(sub.SubscriptionId), "Re-lease must not duplicate the logical delivery.");
        Assert.AreEqual(0, (await host.GetAsync(sub.SubscriptionId)).CursorSequence);
        await coordinator.AckAsync(binding, new(sub.SubscriptionId, [secondRead.Deliveries[0].EventId]));
        Assert.AreEqual(0, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length);
    }

    [TestMethod]
    public async Task SnapshotRequired_WhenTheCursorPredatesRetention()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        host.SetOldestAvailable("ws-1", 2);

        var page = await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10));
        Assert.IsTrue(page.SnapshotRequired);
        Assert.AreEqual(2L, page.OldestAvailableSequence);
        Assert.AreEqual(0, page.Deliveries.Length);
    }

    [TestMethod]
    public async Task Unsubscribed_SubscriptionRejectsFurtherReadsAndAcks()
    {
        var (_, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        await coordinator.UnsubscribeAsync(binding, new(sub.SubscriptionId));
        var read = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10)));
        Assert.AreEqual(EventingErrorCodes.SubscriptionCancelled, read.Code);
        var ack = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.AckAsync(binding, new(sub.SubscriptionId, ["missing"])));
        Assert.AreEqual(EventingErrorCodes.SubscriptionCancelled, ack.Code);
    }

    [TestMethod]
    public async Task UngrantedKind_AndCrossWorkspace_AreRejected()
    {
        var (_, coordinator, binding) = New();
        var denied = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.SubscribeAsync(binding, new("ws-1", ["goal.completed"])));
        Assert.AreEqual(EventingErrorCodes.GrantDenied, denied.Code);
        var crossWorkspace = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.SubscribeAsync(binding, new("ws-2", ["review.completed"])));
        Assert.AreEqual(EventingErrorCodes.RequestInvalid, crossWorkspace.Code);
    }

    [TestMethod]
    public async Task StaleGeneration_CannotReadTheSubscription()
    {
        var (_, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var stale = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            coordinator.ReadAsync(binding with { Generation = 2 }, new(sub.SubscriptionId, 10)));
        Assert.AreEqual(EventingErrorCodes.GenerationStale, stale.Code);
    }

    [TestMethod]
    public async Task TwoDispatchers_NeverDeliverOneEventTwiceAsTwoLogicalEvents()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var dispatcherA = new SubscriptionDispatcher(host, host, host, host);
        var dispatcherB = new SubscriptionDispatcher(host, host, host, host);

        var readA = await dispatcherA.DispatchAsync(sub.SubscriptionId, "worker-a", 10);
        Assert.AreEqual(1, readA.Deliveries.Length);
        var resultB = await dispatcherB.DispatchAsync(sub.SubscriptionId, "worker-b", 10);
        Assert.AreEqual(0, resultB.Deliveries.Length, "A live lease owned by another worker is never re-leased.");
        var reissued = await dispatcherA.DispatchAsync(sub.SubscriptionId, "worker-a", 10);
        Assert.AreEqual(readA.Deliveries[0].DeliveryId, reissued.Deliveries[0].DeliveryId,
            "The same logical event is re-leased to the same owner, never duplicated.");

        await dispatcherA.AckAsync(sub.SubscriptionId, "worker-a", readA.Deliveries,
            [readA.Deliveries[0].DeliveryId]);
        var staleAck = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
        {
            await dispatcherA.AckAsync(sub.SubscriptionId, "worker-a", readA.Deliveries,
                [readA.Deliveries[0].DeliveryId]);
        });
        Assert.AreEqual(EventingErrorCodes.CursorGap, staleAck.Code, "A stale acknowledgement cannot win after the checkpoint advanced.");
    }

    [TestMethod]
    public async Task DeadLetter_IsQueryable_AndReplayRequiresExplicitHostAction()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        host.AppendEvent("ws-1", "review.completed", "ws-1");
        var dispatcher = new SubscriptionDispatcher(host, host, host, host);
        var internalDelivery = (await dispatcher.DispatchAsync(sub.SubscriptionId, "owner-a", 10)).Deliveries[0];

        await dispatcher.FailAsync(sub.SubscriptionId, "owner-a", internalDelivery.DeliveryId, "plugin.error", deadLetter: true);
        Assert.AreEqual(0, host.PendingCount(sub.SubscriptionId));
        Assert.AreEqual(1, host.DeadLetterCount(sub.SubscriptionId));
        Assert.AreEqual(0, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length);

        var pending = await host.ListPendingAsync(sub.SubscriptionId);
        var dead = (await host.ListDeadLettersAsync(sub.SubscriptionId)).Single();
        Assert.AreEqual("plugin.error", dead.FailureCode);
        var requeued = await dispatcher.ReplayDeadLetterAsync(sub.SubscriptionId, dead.DeliveryId);
        Assert.AreEqual(dead.Event.EventId, requeued.Event.EventId);
        Assert.AreEqual(3, requeued.Attempt);
        Assert.AreEqual(1, host.PendingCount(sub.SubscriptionId));
        Assert.AreEqual(0, pending.Length);
    }

    [TestMethod]
    public async Task Outbox_EventIsDeliverableOnlyAfterCommit()
    {
        var (host, coordinator, binding) = New();
        var sub = await coordinator.SubscribeAsync(binding, new("ws-1", ["review.completed"]));
        var outbox = await host.BeginAsync();
        await outbox.AppendAsync(new("evt-1", "ws-1", 0, "review.completed", 1, DateTimeOffset.UtcNow, "ws-1",
            "payload", "hash", "canonical"));
        Assert.AreEqual(0, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length,
            "An uncommitted outbox event is invisible.");
        await outbox.CommitAsync();
        await outbox.DisposeAsync();
        Assert.AreEqual(1, (await coordinator.ReadAsync(binding, new(sub.SubscriptionId, 10))).Deliveries.Length);
    }

    [TestMethod]
    public async Task Outbox_DuplicateEventId_RejectsTheWholeTransaction()
    {
        var (host, _, _) = New();
        var first = await host.BeginAsync();
        await first.AppendAsync(new("evt-dup", "ws-1", 0, "review.completed", 1, DateTimeOffset.UtcNow, "ws-1",
            "payload", "hash", "canonical"));
        await first.CommitAsync();
        var second = await host.BeginAsync();
        await second.AppendAsync(new("evt-dup", "ws-1", 0, "review.completed", 1, DateTimeOffset.UtcNow, "ws-1",
            "payload", "hash", "canonical"));
        var duplicate = await Assert.ThrowsExactlyAsync<EventingException>(async () => await second.CommitAsync());
        Assert.AreEqual(EventingErrorCodes.DuplicateEvent, duplicate.Code);
        var log = await host.ReadAsync(new("ws-1", 0, 10));
        Assert.AreEqual(1, log.Events.Length, "A rejected transaction commits nothing.");
    }

    [TestMethod]
    public async Task Outbox_UncommittedTransaction_LeavesNothingBehind()
    {
        var (host, _, _) = New();
        var outbox = await host.BeginAsync();
        await outbox.AppendAsync(new("evt-2", "ws-1", 0, "review.completed", 1, DateTimeOffset.UtcNow, "ws-1",
            "payload", "hash", "canonical"));
        await outbox.DisposeAsync();
        Assert.AreEqual(0, (await host.ReadAsync(new("ws-1", 0, 10))).Events.Length);
    }

    private static (InMemoryEventingHost Host, DeliveryCoordinator Coordinator, HostPluginBinding Binding) New()
    {
        var host = new InMemoryEventingHost();
        var dispatcher = new SubscriptionDispatcher(host, host, host, host);
        var coordinator = new DeliveryCoordinator(host, dispatcher, host, host);
        return (host, coordinator, EventingTestFixtures.Binding());
    }
}
