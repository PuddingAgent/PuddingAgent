using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Plugin workspace-event delivery: the committed EventDelivery maps to the frozen plugin contract with the host
/// activation identity, only artifact-created events fan out, and "no active subscriber" fails instead of acking.
/// </summary>
[TestClass]
public sealed class PluginWorkspaceEventSinkTests
{
    [TestMethod]
    public async Task ArtifactEvent_IsMappedToHostScopedPluginEvent()
    {
        var target = new FakeTarget { Deliveries = 1 };
        var sink = new PluginManagerEventDeliverySink(target, new FakeSubscriptions());

        await sink.DeliverAsync(Delivery("event-1", PluginEvents.WorkspaceArtifactCreated));

        Assert.AreEqual(1, target.Events.Count);
        var evt = target.Events[0];
        Assert.AreEqual("event-1", evt.EventId);
        Assert.AreEqual(PluginEvents.WorkspaceArtifactCreated, evt.EventName);
        Assert.AreEqual("ws-1", evt.Scope.WorkspaceId);
        Assert.AreEqual("host-dispatch", evt.Scope.ActivationKey);
        Assert.AreEqual(string.Empty, evt.Scope.AgentId, "Host dispatch is independent of any Agent session.");
        Assert.AreEqual("ref://event-1", evt.Payload.GetProperty("payloadRef").GetString());
        Assert.AreEqual("sha-event-1", evt.Payload.GetProperty("payloadHash").GetString());
        CollectionAssert.AreEqual(new[] { "sample.observer" }, target.PluginIds, "delivery is targeted at the subscription grant.");
    }

    [TestMethod]
    public async Task NoActiveSubscriber_FailsInsteadOfAcknowledging()
    {
        var sink = new PluginManagerEventDeliverySink(new FakeTarget { Deliveries = 0 }, new FakeSubscriptions());

        var error = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await sink.DeliverAsync(Delivery("event-1", PluginEvents.WorkspaceArtifactCreated)));
        Assert.AreEqual(EventingErrorCodes.RequestInvalid, error.Code);
    }

    [TestMethod]
    public async Task OtherEventKinds_AreNotFannedOutToPlugins()
    {
        var target = new FakeTarget { Deliveries = 1 };
        var sink = new PluginManagerEventDeliverySink(target, new FakeSubscriptions());

        await sink.DeliverAsync(Delivery("event-2", "review.completed"));

        Assert.AreEqual(0, target.Events.Count);
    }

    [TestMethod]
    public async Task OutboxWorkspaceSource_ListsDistinctCommittedWorkspaces()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
        }
        var uow = new EventOutboxUnitOfWork(factory);
        await AppendAsync(uow, "event-1", "ws-1");
        await AppendAsync(uow, "event-2", "ws-2");
        await AppendAsync(uow, "event-3", "ws-1");

        var workspaces = await new OutboxWorkspaceSource(factory).ListAsync();

        CollectionAssert.AreEquivalent(new[] { "ws-1", "ws-2" }, workspaces.ToArray());
    }

    private static async Task AppendAsync(EventOutboxUnitOfWork uow, string eventId, string workspaceId)
    {
        await uow.ExecuteAsync(async (db, outbox, ct) =>
        {
            await outbox.AppendAsync(new OutboxFact(eventId, workspaceId,
                PluginEvents.WorkspaceArtifactCreated, 1, workspaceId, "ref://" + eventId, "sha-" + eventId,
                "plugin-test", DateTimeOffset.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return 0;
        });
    }

    private static EventDelivery Delivery(string eventId, string kind) => new(
        "delivery-" + eventId, "subscription-1",
        new EventEnvelope(eventId, "ws-1", 1, kind, 1, DateTimeOffset.UtcNow, "ws-1",
            "ref://" + eventId, "sha-" + eventId, "producer"),
        1, 0, DateTimeOffset.UtcNow, null);


    private sealed class FakeSubscriptions : ISubscriptionStore
    {
        public ValueTask<SubscriptionRecord> CreateAsync(SubscriptionGrant grant, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<SubscriptionRecord> GetAsync(string subscriptionId, CancellationToken ct = default)
            => ValueTask.FromResult(new SubscriptionRecord(subscriptionId,
                new SubscriptionGrant("sample.observer", 1, "ws-1",
                    [PluginEvents.WorkspaceArtifactCreated]), 0, 0, DateTimeOffset.UtcNow, false));
        public ValueTask CancelAsync(string subscriptionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<ImmutableArray<SubscriptionRecord>> ListAsync(string workspaceId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTarget : IWorkspacePluginEventTarget
    {
        public int Deliveries { get; init; }
        public List<PluginWorkspaceEvent> Events { get; } = [];
        public List<string> PluginIds { get; } = [];

        public ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
            string pluginId, long generation, CancellationToken ct = default)
        {
            Events.Add(workspaceEvent);
            PluginIds.Add(pluginId);
            IReadOnlyList<PluginEventDelivery> result = Deliveries == 0
                ? []
                : [new PluginEventDelivery("activation-1", pluginId, "archive", true)];
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
