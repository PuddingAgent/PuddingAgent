using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-07A S5 delivery store on real SQLite: enqueue dedupes per subscription/event and leases bounded batches;
/// retry increments attempts; dead letters leave pending/claim, are listed and can be requeued; foreign ids fail.
/// </summary>
[TestClass]
public sealed class EventDeliveryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static EventEnvelope Envelope(string eventId, long sequence) => new(eventId, "stream-1", sequence,
        "review.completed", 1, DateTimeOffset.UtcNow, "ws-1", "ref://" + eventId, "sha-" + eventId, "canonical");

    [TestMethod]
    public async Task EnqueueDedupesAndLeasesBoundedBatches()
    {
        await using var host = await TestHost.CreateAsync();
        var events = ImmutableArray.Create(Envelope("event-1", 1), Envelope("event-2", 2), Envelope("event-3", 3));
        await host.Store.EnqueueAsync("sub-1", events);
        var first = await host.Store.ClaimAsync("sub-1", maxCount: 2, "worker-a", Now, Now.AddMinutes(1));
        CollectionAssert.AreEqual(new[] { "event-1", "event-2" }, first.Select(d => d.Event.EventId).ToArray());
        Assert.AreEqual("worker-a", first[0].LeaseOwner);

        var replay = await host.Store.ClaimAsync("sub-1", maxCount: 3, "worker-a", Now, Now.AddMinutes(1));
        Assert.AreEqual(3, replay.Length, "A replay must not duplicate deliveries.");
        Assert.AreEqual(3, (await host.Store.ListPendingAsync("sub-1")).Length);
    }

    [TestMethod]
    public async Task FailureEitherRetriesOrDeadLetters()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.EnqueueAsync("sub-1", [Envelope("event-1", 1), Envelope("event-2", 2)]);
        var pending = await host.Store.ClaimAsync("sub-1", 2, "worker-a", Now, Now.AddMinutes(1));

        await host.Store.FailAsync("sub-1", pending[0].DeliveryId, "worker-a", "plugin.timeout", deadLetter: false, Now);
        await host.Store.FailAsync("sub-1", pending[1].DeliveryId, "worker-a", "plugin.denied", deadLetter: true, Now);

        var retried = await host.Store.ListPendingAsync("sub-1");
        Assert.AreEqual(1, retried.Length, "A retried delivery stays pending; a dead letter does not.");
        Assert.AreEqual(2, retried[0].Attempt);
        Assert.AreEqual("plugin.timeout", retried[0].FailureCode);

        var deadLetters = await host.Store.ListDeadLettersAsync("sub-1");
        Assert.AreEqual(1, deadLetters.Length);
        var claimed = await host.Store.ClaimAsync("sub-1", 5, "worker-a", Now, Now.AddMinutes(1));
        Assert.AreEqual(1, claimed.Length, "Dead letters are not leased until requeued.");
    }

    [TestMethod]
    public async Task RequeueDeadLetter_BringsItBack_AndNonDeadLetterIsTyped()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.EnqueueAsync("sub-1", [Envelope("event-1", 1)]);
        var delivery = (await host.Store.ClaimAsync("sub-1", 1, "worker-a", Now, Now.AddMinutes(1)))[0];
        var notDead = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.RequeueDeadLetterAsync("sub-1", delivery.DeliveryId));
        Assert.AreEqual(EventingErrorCodes.DeadLetterMissing, notDead.Code);

        await host.Store.FailAsync("sub-1", delivery.DeliveryId, "worker-a", "plugin.denied", deadLetter: true, Now);
        var requeued = await host.Store.RequeueDeadLetterAsync("sub-1", delivery.DeliveryId);
        Assert.AreEqual(3, requeued.Attempt);
        Assert.AreEqual(1, (await host.Store.ListPendingAsync("sub-1")).Length);
        Assert.AreEqual(0, (await host.Store.ListDeadLettersAsync("sub-1")).Length);
    }

    [TestMethod]
    public async Task ForeignDeliveryIdIsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.EnqueueAsync("sub-1", [Envelope("event-1", 1)]);
        var delivery = (await host.Store.ClaimAsync("sub-1", 1, "worker-a", Now, Now.AddMinutes(1)))[0];
        var failure = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.FailAsync("sub-other", delivery.DeliveryId, "worker-a", "x", deadLetter: false, Now));
        Assert.AreEqual(EventingErrorCodes.RequestInvalid, failure.Code);

        var stale = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.FailAsync("sub-1", delivery.DeliveryId, "worker-b", "x", deadLetter: false, Now));
        Assert.AreEqual(EventingErrorCodes.LeaseStale, stale.Code);
    }

    [TestMethod]
    public async Task LiveLease_IsNotHandedToAnotherOwner_UntilItExpires()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.EnqueueAsync("sub-1", [Envelope("event-1", 1)]);
        var first = await host.Store.ClaimAsync("sub-1", 1, "worker-a", Now, Now.AddMinutes(1));
        Assert.AreEqual(1, first.Length);

        var stolen = await host.Store.ClaimAsync("sub-1", 1, "worker-b", Now.AddSeconds(1), Now.AddMinutes(2));
        Assert.AreEqual(0, stolen.Length, "A live lease owned by another worker is never re-leased.");
        var expired = await host.Store.ClaimAsync("sub-1", 1, "worker-b", Now.AddMinutes(2), Now.AddMinutes(3));
        Assert.AreEqual(1, expired.Length, "An expired lease can be reclaimed by another owner.");
        Assert.AreEqual("worker-b", expired[0].LeaseOwner);
        Assert.AreEqual(2, expired[0].Attempt);
    }
    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, EventDeliveryStore store) : IAsyncDisposable
    {
        public EventDeliveryStore Store { get; } = store;

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
            return new(connection, new EventDeliveryStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
