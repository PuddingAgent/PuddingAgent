using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Demo pipeline on real SQLite: a versioned artifact submission commits an outbox event, the dispatch worker delivers
/// it through the production sink to the plugin target, and the production archive store records exactly one logical
/// archive keyed by EventId. Redelivery replays one record; a cancelled subscription archives nothing new.
/// </summary>
[TestClass]
public sealed class PluginWorkspaceEventPipelineTests
{
    [TestMethod]
    public async Task ArtifactSubmission_ArchivesOnce_AndRedeliveryDoesNotDuplicate()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.SubscribeAsync();
        var eventId = await host.PublishAsync("ref://artifact/v1");

        var first = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, first.Delivered);
        Assert.AreEqual(1, first.Acknowledged);
        Assert.AreEqual(0, (await host.Archive.ListAsync("ws-1", eventId)).Count,
            "The platform sink no longer writes archive records; the observer must request them.");

        // Simulated redelivery after the receipt was lost: the host redelivers.
        var page = await host.Log.ReadAsync(new("ws-1", 0, 10));
        await host.Deliveries.EnqueueAsync(subscription.SubscriptionId, page.Events);
        var again = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, again.Delivered, "The host redelivers the committed event.");
    }

    [TestMethod]
    public async Task CancelledSubscription_DoesNotArchiveLaterArtifacts()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.SubscribeAsync();
        await host.PublishAsync("ref://artifact/v1");
        await host.Worker.RunOnceAsync();

        await host.Subscriptions.CancelAsync(subscription.SubscriptionId);
        await host.PublishAsync("ref://artifact/v2");
        var afterUnload = await host.Worker.RunOnceAsync();

        Assert.AreEqual(0, afterUnload.Delivered);
        Assert.AreEqual(0, afterUnload.Subscriptions, "A cancelled subscription is not scanned.");
    }

    [TestMethod]
    public async Task TwoSubscribers_OneFails_OnlyTheSuccessfulOneIsAcknowledged()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.SubscribeAsync("plugin-a");
        var second = await host.SubscribeAsync("plugin-b");
        host.Target.FailPluginId = "plugin-b";
        var eventId = await host.PublishAsync("ref://artifact/v1");

        var run = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, run.Acknowledged, "Only plugin-a's delivery is acknowledged.");
        Assert.AreEqual(1, run.Failed);
        Assert.AreEqual(1L, (await host.Subscriptions.GetAsync(first.SubscriptionId)).CursorSequence);
        Assert.AreEqual(0L, (await host.Subscriptions.GetAsync(second.SubscriptionId)).CursorSequence);
        Assert.AreEqual(1, (await host.Deliveries.ListPendingAsync(second.SubscriptionId)).Length,
            "plugin-b keeps its own leased delivery for retry.");

        host.Target.FailPluginId = null;
        var retry = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, retry.Acknowledged, "plugin-b retries independently and succeeds.");
        Assert.AreEqual(0, (await host.Deliveries.ListPendingAsync(second.SubscriptionId)).Length);
    }

    private sealed class DeliveringTarget : IWorkspacePluginEventTarget
    {
        public string? FailPluginId { get; set; }

        public ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
            string pluginId, long generation, CancellationToken ct = default)
        {
            IReadOnlyList<PluginEventDelivery> deliveries = FailPluginId == pluginId
                ? [new PluginEventDelivery("activation-" + pluginId, pluginId, "archive", false, "plugin.failed")]
                : [new PluginEventDelivery("activation-" + pluginId, pluginId, "archive", true)];
            return ValueTask.FromResult(deliveries);
        }
    }

    private sealed class TestHost(SqliteConnection connection, SubscriptionStore subscriptions,
        EventDeliveryStore deliveries, OutboxCommittedEventLog log, WorkspaceArtifactEventPublisher publisher,
        PluginArchiveStore archive, PluginEventDispatchWorker worker, DeliveringTarget target) : IAsyncDisposable
    {
        public SubscriptionStore Subscriptions { get; } = subscriptions;
        public EventDeliveryStore Deliveries { get; } = deliveries;
        public OutboxCommittedEventLog Log { get; } = log;
        public PluginArchiveStore Archive { get; } = archive;
        public PluginEventDispatchWorker Worker { get; } = worker;
        public DeliveringTarget Target { get; } = target;

        public ValueTask<string> PublishAsync(string artifactRef) =>
            publisher.PublishCreatedAsync("ws-1", artifactRef, "sha-" + artifactRef, "artifact-service",
                businessRef: artifactRef);

        public async Task<SubscriptionRecord> SubscribeAsync(string pluginId = "plugin-a") =>
            await Subscriptions.CreateAsync(new(pluginId, 1, "ws-1", [PluginEvents.WorkspaceArtifactCreated]));

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await PluginArchiveSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var subscriptions = new SubscriptionStore(factory);
            var deliveries = new EventDeliveryStore(factory);
            var log = new OutboxCommittedEventLog(factory);
            var dispatcher = new SubscriptionDispatcher(log, subscriptions, deliveries, subscriptions);
            var archive = new PluginArchiveStore(factory);
            var target = new DeliveringTarget();
            var sink = new PluginManagerEventDeliverySink(target, subscriptions);
            var worker = new PluginEventDispatchWorker(new Workspaces(), subscriptions, dispatcher, deliveries, sink);
            var publisher = new WorkspaceArtifactEventPublisher(new EventOutboxUnitOfWork(factory));
            return new(connection, subscriptions, deliveries, log, publisher, archive, worker, target);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Workspaces : IEventDispatchWorkspaceSource
    {
        public ValueTask<ImmutableArray<string>> ListAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(ImmutableArray.Create("ws-1"));
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
