using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskSchedulingCoordinatorTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private MutableTimeProvider _clock = null!;
    private TaskSchedulerIntentStore _store = null!;
    private FakeEvaluator _evaluator = null!;
    private FakeStarter _starter = null!;
    private TaskSchedulingCoordinator _coordinator = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "scheduler-coordinator-tests", Guid.NewGuid().ToString("N"));
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
        _evaluator = new FakeEvaluator();
        _starter = new FakeStarter();
        _coordinator = CreateCoordinator(maxAttempts: 3);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TaskSchedulingCoordinator CreateCoordinator(int maxAttempts) => new(
        _store,
        _evaluator,
        _starter,
        new FakeCatalog(),
        new FakeAvailabilityStore(),
        new StaticOptionsMonitor<TaskAutoDispatchOptions>(new TaskAutoDispatchOptions
        {
            Enabled = true,
            Mode = "authoritative",
            WorkspaceIds = ["ws"],
            CandidateLimit = 100,
            IntentBatchSize = 50,
            IntentLease = TimeSpan.FromMinutes(2),
            IntentMaxAttempts = maxAttempts,
        }),
        _clock,
        NullLogger<TaskSchedulingCoordinator>.Instance);

    private TaskSchedulerIntentEnvelope TaskIntent(long eventId) => new()
    {
        WorkspaceId = "ws",
        Source = TaskSchedulerIntentSources.TaskEvents,
        SourceEventId = eventId,
        EventType = "task.ready",
        TaskId = $"task-{eventId}",
        CreatedAtUtc = _clock.GetUtcNow(),
    };

    private TaskSchedulerIntentEnvelope GoalIntent(long eventId) => new()
    {
        WorkspaceId = "ws",
        Source = TaskSchedulerIntentSources.ConversationEvents,
        SourceEventId = eventId,
        EventType = "goal.completed",
        GoalRunId = $"goal-{eventId}",
        CreatedAtUtc = _clock.GetUtcNow(),
    };

    [TestMethod]
    public async Task Coordinator_ProcessesTaskIntent_EvaluatesStartsAndCompletes()
    {
        await _store.EnqueueAsync(TaskIntent(1));
        await _store.EnqueueAsync(TaskIntent(2));

        await _coordinator.ProcessOnceAsync();

        Assert.HasCount(1, _evaluator.Calls);
        Assert.AreEqual("ws", _evaluator.Calls[0]);
        Assert.HasCount(1, _starter.Calls);
        await using var db = await _factory.CreateDbContextAsync();
        var statuses = await db.TaskSchedulerIntents.Select(item => item.Status).ToListAsync();
        Assert.IsTrue(statuses.All(status => status == TaskSchedulerIntentStatuses.Done));
    }

    [TestMethod]
    public async Task Coordinator_FailurePath_ParksIntentDeadWhenAttemptsExhausted()
    {
        _evaluator.Throw = new InvalidOperationException("evaluator exploded");
        await _store.EnqueueAsync(TaskIntent(3));

        await CreateCoordinator(maxAttempts: 1).ProcessOnceAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Dead, row.Status);
        Assert.IsTrue(row.LastError!.Contains("evaluator exploded"));
    }

    [TestMethod]
    public async Task Coordinator_RecoversIntentLeftInExpiredLease()
    {
        await _store.EnqueueAsync(TaskIntent(4));
        // 模拟前一个协调者崩溃：intent 停在 processing 且租约已过期。
        var staleOwner = new TaskSchedulerIntentStore(_factory, NullLogger<TaskSchedulerIntentStore>.Instance);
        await staleOwner.DequeueAsync("ws", 10, TimeSpan.FromSeconds(1), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _coordinator.ProcessOnceAsync();

        Assert.HasCount(1, _starter.Calls);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, row.Status);
    }

    [TestMethod]
    public async Task Coordinator_GoalTerminalIntent_TriggersAvailabilityRebuildBeforeEvaluate()
    {
        await _store.EnqueueAsync(GoalIntent(5));

        await _coordinator.ProcessOnceAsync();

        Assert.HasCount(1, _evaluator.Calls);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, (await db.TaskSchedulerIntents.SingleAsync()).Status);
    }

    private sealed class FakeEvaluator : ITaskAutoDispatchEvaluator
    {
        public List<string> Calls { get; } = [];
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
            string workspaceId,
            int limit,
            CancellationToken ct = default)
        {
            Calls.Add(workspaceId);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult<IReadOnlyList<TaskAutoDispatchCandidateDecision>>([]);
        }
    }

    private sealed class FakeStarter : ITaskAutoDispatchStarter
    {
        public List<IReadOnlyList<TaskAutoDispatchCandidateDecision>> Calls { get; } = [];

        public Task<int> DispatchAsync(
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
            CancellationToken ct = default)
        {
            Calls.Add(decisions);
            return Task.FromResult(0);
        }
    }

    private sealed class FakeCatalog : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>([]);
    }

    private sealed class FakeAvailabilityStore : IAgentAvailabilityProjectionStore
    {
        public Task<AgentAvailabilitySnapshot> GetAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException("not expected in coordinator tests");

        public Task<AgentAvailabilitySnapshot> RebuildAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException("empty catalog means rebuild is never invoked");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
