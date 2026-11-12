using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 / 07A outbox dispatcher on real SQLite: unpublished completed facts are delivered to the sink with the
/// full logical key and marked published only on success; a failed sink call leaves the row unpublished for retry;
/// published rows are never re-delivered; batches are bounded and ordered.
/// </summary>
[TestClass]
public sealed class ReviewEventDispatcherTests
{
    [TestMethod]
    public async Task PublishesUnpublishedAndMarksThemOnce()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-1", revision: 3));
        var sink = new FakeSink();
        var dispatcher = new ReviewEventDispatcher(host.Factory, sink);

        var result = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(1, result.Published);
        Assert.AreEqual(1, sink.Events.Count);
        Assert.AreEqual("goal-1", sink.Events[0].Key.GoalRunId);
        Assert.AreEqual(2, sink.Events[0].Key.IterationNo);
        Assert.AreEqual("artifact-hash", sink.Events[0].Key.ArtifactSetHash);

        var second = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(0, second.Published);
        Assert.AreEqual(1, sink.Events.Count);
    }

    [TestMethod]
    public async Task SinkFailureLeavesTheRowUnpublishedForRetry()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-1", revision: 3));
        var sink = new FakeSink { Fail = true };
        var dispatcher = new ReviewEventDispatcher(host.Factory, sink);

        var failed = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(1, failed.Failed);
        Assert.AreEqual(0, failed.Published);
        Assert.AreEqual(0, await host.PublishedCountAsync());

        sink.Fail = false;
        var retried = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(1, retried.Published);
        Assert.AreEqual(1, await host.PublishedCountAsync());
    }

    [TestMethod]
    public async Task BatchIsBoundedAndOrdered()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-late", revision: 1, occurredAt: DateTimeOffset.UtcNow));
        await host.Outbox.PublishCompletedAsync(Event("event-early", revision: 2,
            occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var sink = new FakeSink();
        var dispatcher = new ReviewEventDispatcher(host.Factory, sink);

        var result = await dispatcher.DispatchBatchAsync(1);
        Assert.AreEqual(1, result.Published);
        Assert.AreEqual("event-early", sink.Events[0].EventId, "The oldest unpublished fact is delivered first.");

        var rest = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(1, rest.Published);
        Assert.AreEqual("event-late", sink.Events[1].EventId);
    }

    [TestMethod]
    public async Task AlreadyPublishedRowsAreNotDelivered()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-1", revision: 1));
        await host.MarkPublishedAsync("event-1");
        var sink = new FakeSink();
        var result = await new ReviewEventDispatcher(host.Factory, sink).DispatchBatchAsync(10);
        Assert.AreEqual(0, result.Attempted);
        Assert.AreEqual(0, sink.Events.Count);
    }

    private static ReviewCompletedEvent Event(string eventId, long revision,
        DateTimeOffset? occurredAt = null) => new(eventId, "review-1", "job-1",
        new("ws-1", "work-1", "goal-1", 3, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        ReviewFindingValue.NeedsWork, ReviewEvidenceLevel.Observed, revision,
        occurredAt ?? DateTimeOffset.UtcNow);

    private sealed class FakeSink : IReviewCompletionSink
    {
        public bool Fail { get; set; }
        public List<ReviewCompletedEvent> Events { get; } = [];

        public ValueTask PublishAsync(ReviewCompletedEvent completed, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("sink down");
            Events.Add(completed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory,
        ReviewEventOutbox outbox) : IAsyncDisposable
    {
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;
        public ReviewEventOutbox Outbox { get; } = outbox;

        public async Task<int> PublishedCountAsync()
        {
            await using var db = factory.CreateDbContext();
            return await db.ReviewEvents.CountAsync(e => e.Published);
        }

        public async Task MarkPublishedAsync(string eventId)
        {
            await using var db = factory.CreateDbContext();
            var row = await db.ReviewEvents.SingleAsync(e => e.EventId == eventId);
            row.Published = true;
            await db.SaveChangesAsync();
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
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, factory, new ReviewEventOutbox(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
