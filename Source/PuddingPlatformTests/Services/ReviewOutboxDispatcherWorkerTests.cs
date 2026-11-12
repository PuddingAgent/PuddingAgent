using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 outbox dispatch loop: one pass drains unpublished facts through the sink; the loop stops promptly on
/// cancellation; a failed sink pass leaves rows unpublished (durable retry), not silently acknowledged.
/// </summary>
[TestClass]
public sealed class ReviewOutboxDispatcherWorkerTests
{
    [TestMethod]
    public async Task DispatchOnce_PublishesAndMarks()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-1"));
        var sink = new FakeSink();
        var worker = new ReviewOutboxDispatcherWorker(new ReviewEventDispatcher(host.Factory, sink),
            interval: TimeSpan.FromMilliseconds(20));

        var result = await worker.DispatchOnceAsync();
        Assert.AreEqual(1, result.Published);
        Assert.AreEqual(1, sink.Events.Count);
        Assert.AreEqual(0, (await worker.DispatchOnceAsync()).Attempted);
    }

    [TestMethod]
    public async Task LoopStopsOnCancellation_AndFailedSinkKeepsTheRowUnpublished()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Outbox.PublishCompletedAsync(Event("event-1"));
        var sink = new FakeSink { Fail = true };
        var worker = new ReviewOutboxDispatcherWorker(new ReviewEventDispatcher(host.Factory, sink),
            interval: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, await host.PublishedCountAsync(), "A failed delivery must stay unpublished.");
        Assert.IsTrue(sink.Attempts >= 1, "The loop must have attempted delivery at least once.");
    }

    private static ReviewCompletedEvent Event(string eventId) => new(eventId, "review-1", "job-1",
        new("ws-1", "work-1", "goal-1", 3, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        ReviewFindingValue.Satisfied, ReviewEvidenceLevel.CheckVerified, 5, DateTimeOffset.UtcNow);

    private sealed class FakeSink : IReviewCompletionSink
    {
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public List<ReviewCompletedEvent> Events { get; } = [];

        public ValueTask PublishAsync(ReviewCompletedEvent completed, CancellationToken ct = default)
        {
            Attempts++;
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

    private sealed class TestHost(SqliteConnection connection, string dbPath,
        IDbContextFactory<PlatformDbContext> factory, ReviewEventOutbox outbox) : IAsyncDisposable
    {
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;
        public ReviewEventOutbox Outbox { get; } = outbox;

        public async Task<int> PublishedCountAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.ReviewEvents.CountAsync(e => e.Published);
        }

        public static async Task<TestHost> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "pudding-review-outbox-" + Guid.NewGuid().ToString("N") + ".db");
            var services = new ServiceCollection();
            services.AddDbContextFactory<PlatformDbContext>(options => options.UseSqlite($"Data Source={dbPath};Pooling=False"));
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            await connection.OpenAsync();
            return new(connection, dbPath, factory, new ReviewEventOutbox(factory));
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
