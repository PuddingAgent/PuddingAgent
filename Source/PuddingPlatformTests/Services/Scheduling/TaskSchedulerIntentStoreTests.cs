using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskSchedulerIntentStoreTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private MutableTimeProvider _clock = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "scheduler-intent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(db);
        _clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TaskSchedulerIntentStore CreateStore() => new(
        _factory,
        NullLogger<TaskSchedulerIntentStore>.Instance);

    private TaskSchedulerIntentEnvelope Envelope(long eventId, string source = TaskSchedulerIntentSources.TaskEvents) => new()
    {
        WorkspaceId = "ws",
        Source = source,
        SourceEventId = eventId,
        EventType = source == TaskSchedulerIntentSources.TaskEvents ? "task.ready" : "goal.completed",
        TaskId = source == TaskSchedulerIntentSources.TaskEvents ? "task-1" : null,
        GoalRunId = source == TaskSchedulerIntentSources.ConversationEvents ? "goal-1" : null,
        PayloadJson = """{"k":"v"}""",
        CreatedAtUtc = _clock.GetUtcNow(),
    };

    [TestMethod]
    public async Task Enqueue_IsIdempotentPerSourceEvent()
    {
        var store = CreateStore();
        Assert.IsTrue(await store.EnqueueAsync(Envelope(42)));
        Assert.IsFalse(await store.EnqueueAsync(Envelope(42)));

        // 同一账本行 id 在另一个来源下不冲突（唯一键 = (source, source_event_id)）。
        Assert.IsTrue(await store.EnqueueAsync(Envelope(42, TaskSchedulerIntentSources.ConversationEvents)));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(2, await db.TaskSchedulerIntents.LongCountAsync());
    }

    [TestMethod]
    public async Task Dequeue_IsMutexBetweenConcurrentOwners()
    {
        var first = CreateStore();
        var second = CreateStore();
        Assert.IsTrue(await first.EnqueueAsync(Envelope(7)));

        var claimed = await Task.WhenAll(
            first.DequeueAsync("ws", 10, TimeSpan.FromMinutes(2), _clock.GetUtcNow()),
            second.DequeueAsync("ws", 10, TimeSpan.FromMinutes(2), _clock.GetUtcNow()));

        Assert.HasCount(1, claimed.Where(batch => batch.Count == 1));
        Assert.HasCount(1, claimed.Where(batch => batch.Count == 0));

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Processing, row.Status);
        Assert.AreEqual(1, row.AttemptCount);
    }

    [TestMethod]
    public async Task ExpiredLease_IsReclaimedByNextOwner()
    {
        var first = CreateStore();
        var second = CreateStore();
        await first.EnqueueAsync(Envelope(9));
        var lease = await first.DequeueAsync("ws", 10, TimeSpan.FromMinutes(1), _clock.GetUtcNow());
        Assert.HasCount(1, lease);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = await second.DequeueAsync("ws", 10, TimeSpan.FromMinutes(2), _clock.GetUtcNow());

        Assert.HasCount(1, reclaimed);
        Assert.AreEqual(lease[0].IntentId, reclaimed[0].IntentId);
        Assert.AreEqual(2, reclaimed[0].AttemptCount);
    }

    [TestMethod]
    public async Task FailAsync_ParksDeadAfterMaxAttempts()
    {
        var store = CreateStore();
        await store.EnqueueAsync(Envelope(11));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var batch = await store.DequeueAsync("ws", 10, TimeSpan.FromMinutes(2), _clock.GetUtcNow());
            Assert.HasCount(1, batch);
            var dead = await store.FailAsync(batch[0].IntentId, "boom", maxAttempts: 3, _clock.GetUtcNow());
            Assert.AreEqual(attempt == 3, dead);
        }

        Assert.HasCount(0, await store.DequeueAsync("ws", 10, TimeSpan.FromMinutes(2), _clock.GetUtcNow()));
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Dead, row.Status);
        Assert.IsNotNull(row.LastError);
    }

    [TestMethod]
    public async Task GetTailCursor_MatchesMaxSourceEventId()
    {
        var store = CreateStore();
        Assert.AreEqual(0, await store.GetTailCursorAsync(TaskSchedulerIntentSources.TaskEvents));
        await store.EnqueueAsync(Envelope(7));
        await store.EnqueueAsync(Envelope(42));
        await store.EnqueueAsync(Envelope(5, TaskSchedulerIntentSources.ConversationEvents));
        Assert.AreEqual(42, await store.GetTailCursorAsync(TaskSchedulerIntentSources.TaskEvents));
        Assert.AreEqual(5, await store.GetTailCursorAsync(TaskSchedulerIntentSources.ConversationEvents));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
