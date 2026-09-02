using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskEventLedgerTailBridgeTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private MutableTimeProvider _clock = null!;
    private TaskSchedulerIntentStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "scheduler-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(db);
        _clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        _store = new TaskSchedulerIntentStore(_factory, NullLogger<TaskSchedulerIntentStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TaskEventLedgerTailBridge CreateBridge(bool authoritative = true, string? modeOverride = null) => new(
        _factory,
        _store,
        new StaticOptionsMonitor<TaskAutoDispatchOptions>(new TaskAutoDispatchOptions
        {
            Enabled = true,
            EventDrivenEnabled = true,
            Mode = modeOverride ?? (authoritative ? "authoritative" : "shadow"),
            WorkspaceIds = ["ws"],
            IntentBatchSize = 50,
            IntentPollInterval = TimeSpan.FromSeconds(2),
        }),
        _clock,
        NullLogger<TaskEventLedgerTailBridge>.Instance);

    private async Task<long> InsertTaskEventAsync(
        TaskEventType eventType,
        string taskId = "task-1",
        string workspaceId = "ws")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var sequence = await db.TaskEvents
            .Where(item => item.TaskId == taskId)
            .LongCountAsync() + 1;
        var entity = new TaskEventEntity
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = sequence,
            EventType = eventType,
            CreatedAtUtc = _clock.GetUtcNow(),
        };
        db.TaskEvents.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task<long> InsertConversationEventAsync(string type, string? runId = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var entity = new ConversationEventEntity
        {
            ConversationId = "conv-1",
            Sequence = await db.ConversationEvents
                .Where(item => item.ConversationId == "conv-1")
                .LongCountAsync(),
            EventId = $"cevt-{Guid.NewGuid():N}",
            WorkspaceId = "ws",
            TurnId = "turn-1",
            RunId = runId,
            Type = type,
            Payload = "{}",
            OccurredAt = _clock.GetUtcNow().ToString("O"),
            CommittedAt = _clock.GetUtcNow().ToString("O"),
        };
        db.ConversationEvents.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task<int> CountIntentsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.TaskSchedulerIntents.CountAsync();
    }

    [TestMethod]
    public async Task Startup_DoesNotReplayHistory()
    {
        await InsertTaskEventAsync(TaskEventType.TaskReady);
        Assert.AreEqual(0, await CountTailMaxAsync("task_events"));

        // 桥在账本已有历史行之后才创建：游标初始化为当前 MAX，不回放。
        var bridge = CreateBridge();
        await bridge.PollOnceAsync();

        Assert.AreEqual(0, await CountIntentsAsync());
    }

    [TestMethod]
    public async Task Poll_EnqueuesOnlyFilteredEvents()
    {
        var bridge = CreateBridge();
        await bridge.PollOnceAsync(); // 空表：游标 = 0

        var ignored = await InsertTaskEventAsync(TaskEventType.TaskCreated);
        var ready = await InsertTaskEventAsync(TaskEventType.TaskReady);
        var convCreated = await InsertConversationEventAsync("goal.created");
        var convCompleted = await InsertConversationEventAsync("goal.completed", runId: "goal-9");
        await bridge.PollOnceAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var intents = await db.TaskSchedulerIntents.OrderBy(item => item.Source).ThenBy(item => item.SourceEventId).ToListAsync();
        Assert.HasCount(2, intents);

        var taskIntent = intents.Single(item => item.Source == TaskSchedulerIntentSources.TaskEvents);
        Assert.AreEqual(ready, taskIntent.SourceEventId);
        Assert.AreEqual("task.ready", taskIntent.EventType);
        Assert.AreEqual("task-1", taskIntent.TaskId);

        var goalIntent = intents.Single(item => item.Source == TaskSchedulerIntentSources.ConversationEvents);
        Assert.AreEqual(convCompleted, goalIntent.SourceEventId);
        Assert.AreEqual("goal.completed", goalIntent.EventType);
        Assert.AreEqual("goal-9", goalIntent.GoalRunId);
    }

    [TestMethod]
    public async Task Restart_ContinuesFromTailWithoutReplay()
    {
        var first = CreateBridge();
        await first.PollOnceAsync();
        await InsertTaskEventAsync(TaskEventType.TaskReady);
        await first.PollOnceAsync();
        Assert.AreEqual(1, await CountIntentsAsync());

        // 重启：新桥实例游标 = MAX(source_event_id)，已入队行不再重复入队。
        var restarted = CreateBridge();
        await restarted.PollOnceAsync();
        Assert.AreEqual(1, await CountIntentsAsync());

        var completed = await InsertTaskEventAsync(TaskEventType.TaskCompleted);
        await restarted.PollOnceAsync();
        Assert.AreEqual(2, await CountIntentsAsync());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.IsTrue(await db.TaskSchedulerIntents.AnyAsync(item =>
            item.Source == TaskSchedulerIntentSources.TaskEvents && item.SourceEventId == completed));
    }

    [TestMethod]
    public async Task ShadowMode_SkipsEnqueue_ButStillAdvancesCursor()
    {
        var shadow = CreateBridge(authoritative: false);
        await shadow.PollOnceAsync();
        var ready = await InsertTaskEventAsync(TaskEventType.TaskReady);
        await shadow.PollOnceAsync();
        Assert.AreEqual(0, await CountIntentsAsync());

        // shadow 期间游标照常推进：切回 authoritative 也不补队列（Worker 5m 扫描兜底）。
        var authoritative = CreateBridge(authoritative: true);
        await authoritative.PollOnceAsync();
        Assert.AreEqual(0, await CountIntentsAsync());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.IsFalse(await db.TaskSchedulerIntents.AnyAsync(item => item.SourceEventId == ready));
    }

    [TestMethod]
    public async Task AuthoritativeSingleMode_EnqueuesTaskAndGoalEvents()
    {
        // authoritative-single 归一后与 authoritative 同走事件入队路径（此前精确匹配 "authoritative" 被排除）。
        var bridge = CreateBridge(modeOverride: "authoritative-single");
        await bridge.PollOnceAsync(); // 空表：游标 = 0

        var ready = await InsertTaskEventAsync(TaskEventType.TaskReady);
        var convCompleted = await InsertConversationEventAsync("goal.completed", runId: "goal-1");
        await bridge.PollOnceAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var intents = await db.TaskSchedulerIntents.OrderBy(item => item.Source).ThenBy(item => item.SourceEventId).ToListAsync();
        Assert.HasCount(2, intents);
        Assert.IsTrue(intents.Any(item =>
            item.Source == TaskSchedulerIntentSources.TaskEvents && item.SourceEventId == ready));
        Assert.IsTrue(intents.Any(item =>
            item.Source == TaskSchedulerIntentSources.ConversationEvents && item.SourceEventId == convCompleted));
    }

    private async Task<long> CountTailMaxAsync(string source) => await _store.GetTailCursorAsync(source);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
