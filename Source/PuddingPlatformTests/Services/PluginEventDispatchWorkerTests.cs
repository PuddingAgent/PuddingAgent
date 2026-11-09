using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-07A S5 dispatch loop on real SQLite: committed outbox facts are delivered through the plugin sink in order,
/// only the contiguous succeeded prefix is acknowledged, failures stay leased for retry and dead-letter after the cap,
/// and another worker's live lease is never taken over.
/// </summary>
[TestClass]
public sealed class PluginEventDispatchWorkerTests
{
    [TestMethod]
    public async Task DeliversAndAcknowledgesCommittedFacts()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync("event-1", "review.completed", 1);
        var subscription = await host.SubscribeAsync();

        var result = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, result.Delivered);
        Assert.AreEqual(1, result.Acknowledged);
        CollectionAssert.AreEqual(new[] { "event-1" }, host.Sink.Events.ToArray());
        Assert.AreEqual(1L, (await host.Subscriptions.GetAsync(subscription.SubscriptionId)).CursorSequence);
        Assert.AreEqual(0, (await host.Deliveries.ListPendingAsync(subscription.SubscriptionId)).Length);
    }

    [TestMethod]
    public async Task SinkFailure_KeepsTheLeaseForRetry_ThenSucceeds()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync("event-1", "review.completed", 1);
        var subscription = await host.SubscribeAsync();

        host.Sink.Throw = true;
        var failedRun = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, failedRun.Failed);
        Assert.AreEqual(0, failedRun.Acknowledged);
        Assert.AreEqual(0L, (await host.Subscriptions.GetAsync(subscription.SubscriptionId)).CursorSequence);
        var pending = await host.Deliveries.ListPendingAsync(subscription.SubscriptionId);
        Assert.AreEqual(1, pending.Length);
        Assert.AreEqual(2, pending[0].Attempt, "Claim and failure each advance the attempt counter.");

        host.Sink.Throw = false;
        var retryRun = await host.Worker.RunOnceAsync();
        Assert.AreEqual(1, retryRun.Acknowledged);
        CollectionAssert.AreEqual(new[] { "event-1" }, host.Sink.Events.ToArray(), "The logical event is delivered once after retry.");
        Assert.AreEqual(1L, (await host.Subscriptions.GetAsync(subscription.SubscriptionId)).CursorSequence);
    }

    [TestMethod]
    public async Task RepeatedFailure_DeadLettersAndBlocksTheCursor()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync("event-1", "review.completed", 1);
        var subscription = await host.SubscribeAsync();
        host.Sink.Throw = true;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await host.Worker.RunOnceAsync();
        }

        var dead = await host.Deliveries.ListDeadLettersAsync(subscription.SubscriptionId);
        Assert.AreEqual(1, dead.Length);
        Assert.AreEqual(0L, (await host.Subscriptions.GetAsync(subscription.SubscriptionId)).CursorSequence,
            "A dead letter blocks the checkpoint until explicitly replayed.");
    }

    [TestMethod]
    public async Task AnotherWorkersLiveLease_IsNotTakenOver()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync("event-1", "review.completed", 1);
        var subscription = await host.SubscribeAsync();
        var other = new SubscriptionDispatcher(host.Log, host.Subscriptions, host.Deliveries, host.Subscriptions);
        var claimed = await other.DispatchAsync(subscription.SubscriptionId, "other-worker", 10);
        Assert.AreEqual(1, claimed.Deliveries.Length);

        var result = await host.Worker.RunOnceAsync();
        Assert.AreEqual(0, result.Delivered);
        Assert.AreEqual(0, host.Sink.Events.Count);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeWorkspaces(ImmutableArray<string> ids) : IEventDispatchWorkspaceSource
    {
        public ValueTask<ImmutableArray<string>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult(ids);
    }

    private sealed class FakeSink : IPluginEventDeliverySink
    {
        public bool Throw { get; set; }
        public List<string> Events { get; } = [];

        public ValueTask DeliverAsync(EventDelivery delivery, CancellationToken ct = default)
        {
            if (Throw) throw new EventingException(EventingErrorCodes.RequestInvalid, "plugin callback failed");
            Events.Add(delivery.Event.EventId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory,
        EventOutboxUnitOfWork uow, SubscriptionStore subscriptions, EventDeliveryStore deliveries,
        OutboxCommittedEventLog log, FakeSink sink, PluginEventDispatchWorker worker) : IAsyncDisposable
    {
        public EventOutboxUnitOfWork Uow { get; } = uow;
        public SubscriptionStore Subscriptions { get; } = subscriptions;
        public EventDeliveryStore Deliveries { get; } = deliveries;
        public OutboxCommittedEventLog Log { get; } = log;
        public FakeSink Sink { get; } = sink;
        public PluginEventDispatchWorker Worker { get; } = worker;

        public async Task AppendAsync(string eventId, string kind, long sequence)
        {
            await Uow.ExecuteAsync(async (db, outbox, ct) =>
            {
                await outbox.AppendAsync(new OutboxFact(eventId, "ws-1", kind, 1, "ws-1", "ref://" + eventId,
                    "sha-" + eventId, "review", DateTimeOffset.UtcNow), ct);
                await db.SaveChangesAsync(ct);
                return 0;
            });
            // The stream sequence is assigned by the UoW; this seed helper only guarantees the fact exists.
        }

        public async Task<SubscriptionRecord> SubscribeAsync() => await Subscriptions.CreateAsync(
            new("plugin-a", 1, "ws-1", ["review.completed"]));

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var uow = new EventOutboxUnitOfWork(factory);
            var subscriptions = new SubscriptionStore(factory);
            var deliveries = new EventDeliveryStore(factory);
            var log = new OutboxCommittedEventLog(factory);
            var dispatcher = new SubscriptionDispatcher(log, subscriptions, deliveries, subscriptions);
            var sink = new FakeSink();
            var worker = new PluginEventDispatchWorker(new FakeWorkspaces(["ws-1"]), subscriptions, dispatcher,
                deliveries, sink);
            return new(connection, factory, uow, subscriptions, deliveries, log, sink, worker);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
