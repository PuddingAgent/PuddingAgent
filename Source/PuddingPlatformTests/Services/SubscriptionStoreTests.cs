using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Eventing;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-07A S5 subscription/checkpoint store on real SQLite: grant creates a durable subscription, ack+advance is one
/// transaction fenced by revision and cursor monotonicity, a foreign delivery id fails the whole batch, and a
/// cancelled subscription cannot advance.
/// </summary>
[TestClass]
public sealed class SubscriptionStoreTests
{
    private static readonly SubscriptionGrant Grant = new("plugin-a", 3, "ws-1", ["review.completed", "tool.result"]);

    [TestMethod]
    public async Task CreateGetAndList_AreWorkspaceScoped()
    {
        await using var host = await TestHost.CreateAsync();
        var created = await host.Store.CreateAsync(Grant);
        Assert.AreEqual(0L, created.CursorSequence);
        Assert.AreEqual(0L, created.Revision);
        Assert.IsFalse(created.Cancelled);
        Assert.AreEqual(2, created.Grant.Kinds.Length);

        var fetched = await host.Store.GetAsync(created.SubscriptionId);
        Assert.AreEqual(created.SubscriptionId, fetched.SubscriptionId);
        Assert.AreEqual(1, (await host.Store.ListAsync("ws-1")).Length);
        Assert.AreEqual(0, (await host.Store.ListAsync("ws-2")).Length);

        var invalid = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.CreateAsync(Grant with { Kinds = [] }));
        Assert.AreEqual(EventingErrorCodes.GrantMissing, invalid.Code);
    }

    [TestMethod]
    public async Task AckAdvancesCursorAndRemovesDeliveries_InOneTransaction()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.Store.CreateAsync(Grant);
        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-1", "event-1", 5);
        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-2", "event-2", 6);

        var updated = await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-1", "delivery-2"], 6, 0);
        Assert.AreEqual(6L, updated.CursorSequence);
        Assert.AreEqual(1L, updated.Revision);
        Assert.AreEqual(0, await host.DeliveryCountAsync(subscription.SubscriptionId));
    }

    [TestMethod]
    public async Task RevisionConflict_CursorGapAndForeignDeliveryAreRejected()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.Store.CreateAsync(Grant);
        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-1", "event-1", 5);

        var revision = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-1"], 5, 9));
        Assert.AreEqual(EventingErrorCodes.CursorConflict, revision.Code);

        var gap = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-1"], 4, 0));
        Assert.AreEqual(EventingErrorCodes.CursorGap, gap.Code);

        await host.InsertDeliveryAsync("other-sub", "delivery-foreign", "event-foreign", 7);
        var foreign = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-1", "delivery-foreign"], 7, 0));
        Assert.AreEqual(EventingErrorCodes.RequestInvalid, foreign.Code);
        Assert.AreEqual(1, await host.DeliveryCountAsync(subscription.SubscriptionId),
            "A failed batch must not consume any delivery.");
    }

    [TestMethod]
    public async Task AdvanceScan_IsMonotonicRevisionFencedAndBlockedByPendingOrDeadLetter()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.Store.CreateAsync(Grant);
        var advanced = await host.Store.AdvanceScanAsync(subscription.SubscriptionId, 3, 0);
        Assert.AreEqual(3L, advanced.ScannedThroughSequence);
        Assert.AreEqual(0L, advanced.CursorSequence, "Scan progress is not an acknowledgement.");
        Assert.AreEqual(1L, advanced.Revision);

        var backwards = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AdvanceScanAsync(subscription.SubscriptionId, 2, advanced.Revision));
        Assert.AreEqual(EventingErrorCodes.CursorGap, backwards.Code);

        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-pending", "event-pending", 6);
        var blockedByPending = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AdvanceScanAsync(subscription.SubscriptionId, 6, advanced.Revision));
        Assert.AreEqual(EventingErrorCodes.CursorGap, blockedByPending.Code);

        var second = await host.Store.CreateAsync(Grant);
        await host.InsertDeliveryAsync(second.SubscriptionId, "delivery-dead", "event-dead", 5, deadLettered: true);
        var blockedByDeadLetter = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AdvanceScanAsync(second.SubscriptionId, 5, 0));
        Assert.AreEqual(EventingErrorCodes.DeadLetterBlocked, blockedByDeadLetter.Code);
    }

    [TestMethod]
    public async Task Ack_IsBlockedByAnOlderDeadLetter_AndUpdatesScanPosition()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.Store.CreateAsync(Grant);
        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-dead", "event-dead", 5, deadLettered: true);
        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-live", "event-live", 6);

        var blocked = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-live"], 6, 0));
        Assert.AreEqual(EventingErrorCodes.DeadLetterBlocked, blocked.Code);
        Assert.AreEqual(0L, (await host.Store.GetAsync(subscription.SubscriptionId)).CursorSequence);
    }
    [TestMethod]
    public async Task CancelledSubscription_CannotAdvance_AndCancelIsIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        var subscription = await host.Store.CreateAsync(Grant);
        await host.Store.CancelAsync(subscription.SubscriptionId);
        var cancelled = await host.Store.GetAsync(subscription.SubscriptionId);
        Assert.IsTrue(cancelled.Cancelled);

        await host.InsertDeliveryAsync(subscription.SubscriptionId, "delivery-1", "event-1", 5);
        var failure = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.AckAndAdvanceAsync(subscription.SubscriptionId, "owner-1", ["delivery-1"], 5, cancelled.Revision));
        Assert.AreEqual(EventingErrorCodes.SubscriptionCancelled, failure.Code);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory,
        SubscriptionStore store) : IAsyncDisposable
    {
        public SubscriptionStore Store { get; } = store;

        public async Task InsertDeliveryAsync(string subscriptionId, string deliveryId, string eventId, long sequence,
            bool deadLettered = false)
        {
            await using var db = factory.CreateDbContext();
            db.EventDeliveries.Add(new EventDeliveryEntity
            {
                DeliveryId = deliveryId,
                SubscriptionId = subscriptionId,
                EventId = eventId,
                EventSequence = sequence,
                EnvelopeJson = "{}",
                Attempt = 1,
                ClaimedAt = DateTimeOffset.UtcNow,
                DeadLettered = deadLettered,
                FailureCode = deadLettered ? "plugin.error" : null,
                LeaseOwnerId = deadLettered ? null : "owner-1",
                LeaseUntilUnixMs = deadLettered ? null : DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            });
            await db.SaveChangesAsync();
        }

        public async Task<int> DeliveryCountAsync(string subscriptionId)
        {
            await using var db = factory.CreateDbContext();
            return await db.EventDeliveries.CountAsync(d => d.SubscriptionId == subscriptionId);
        }

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
            return new(connection, factory, new SubscriptionStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
