using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

/// <summary>
/// 事件驱动 Coordinator 结算合同测试（卡 3bd2a4b0 scheduler-intent part 1/2）：
/// 每个 Intent 必须先落 decision/outcome 再 done（§3.2/§5.3）；authoritative 决策落库失败
/// fail closed（不启动、不结算）；crash 后同 Intent 重放不产生重复 outcome。
/// </summary>
[TestClass]
public sealed class TaskSchedulingCoordinatorTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private MutableTimeProvider _clock = null!;
    private TaskSchedulerIntentStore _store = null!;
    private TaskSchedulerDecisionStore _decisionStore = null!;
    private TaskSchedulerIntentOutcomeStore _outcomeStore = null!;
    private FakeEvaluator _evaluator = null!;
    private FakeStarter _starter = null!;

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
        await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(db);
        await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(db);
        _clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        _store = new TaskSchedulerIntentStore(_factory, NullLogger<TaskSchedulerIntentStore>.Instance);
        _decisionStore = new TaskSchedulerDecisionStore(_factory);
        _outcomeStore = new TaskSchedulerIntentOutcomeStore(_factory);
        _evaluator = new FakeEvaluator();
        _starter = new FakeStarter();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TaskSchedulingCoordinator CreateCoordinator(
        int maxAttempts,
        bool paused = false,
        int maxStartsPerScan = 2,
        string mode = "authoritative",
        IDbContextFactory<PlatformDbContext>? brokenDecisionDbFactory = null) => new(
        _store,
        _evaluator,
        _starter,
        new FakeCatalog(),
        new FakeAvailabilityStore(),
        brokenDecisionDbFactory is null
            ? _decisionStore
            : new TaskSchedulerDecisionStore(brokenDecisionDbFactory),
        _outcomeStore,
        _factory,
        new StaticOptionsMonitor<TaskAutoDispatchOptions>(new TaskAutoDispatchOptions
        {
            Enabled = true,
            EventDrivenEnabled = true,
            Mode = mode,
            WorkspaceIds = ["ws"],
            PausedWorkspaceIds = paused ? ["ws"] : [],
            CandidateLimit = 100,
            MaxStartsPerScan = maxStartsPerScan,
            PolicyRevision = 7,
            IntentBatchSize = 50,
            IntentLease = TimeSpan.FromMinutes(2),
            IntentMaxAttempts = maxAttempts,
        }),
        _clock,
        NullLogger<TaskSchedulingCoordinator>.Instance);

    private TaskSchedulerIntentEnvelope TaskIntent(long eventId, string? taskId = null) => new()
    {
        WorkspaceId = "ws",
        Source = TaskSchedulerIntentSources.TaskEvents,
        SourceEventId = eventId,
        EventType = "task.ready",
        TaskId = taskId ?? $"task-{eventId}",
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

    private async Task SeedReadyTaskAsync(string taskId, bool autoDispatch = true, WorkspaceTaskStatus status = WorkspaceTaskStatus.Ready)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = "ws",
            Title = $"seed-{taskId}",
            Status = status,
            AutoDispatchEnabled = autoDispatch,
        });
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Coordinator_TaskIntent_PersistsDecisionAndOutcomeBeforeDone()
    {
        await SeedReadyTaskAsync("task-1");
        await _store.EnqueueAsync(TaskIntent(1));

        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var intent = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, intent.Status);

        // decision 先于派发落库，scan_id 使用事件批次约定 event-{workspace}-{batchId}。
        var decisions = await db.Database
            .SqlQuery<string>($"""
                SELECT scan_id AS Value FROM task_scheduler_decisions
                """)
            .ToListAsync();
        Assert.HasCount(1, decisions);
        Assert.StartsWith("event-ws-", decisions[0]);

        // outcome 覆盖本 Intent，且关联 decision 与 starter 返回的 Assignment/Goal id。
        var outcome = await _outcomeStore.GetOutcomeAsync(intent.IntentId);
        Assert.IsNotNull(outcome);
        Assert.AreEqual(TaskSchedulerIntentOutcomes.Started, outcome.Outcome);
        Assert.AreEqual("task-1", outcome.TaskId);
        Assert.AreEqual(decisions[0], outcome.ScanId);
        Assert.AreEqual("asg-task-1", outcome.StartedAssignmentId);
        Assert.AreEqual("goal-task-1", outcome.StartedGoalRunId);
        Assert.IsNotNull(outcome.DecisionId);
        Assert.IsGreaterThan(0, outcome.PolicyRevision);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.OptionsHash));
    }

    [TestMethod]
    public async Task Coordinator_NotEvaluatableTask_WritesTerminalOrIneligibleOutcome()
    {
        await SeedReadyTaskAsync("task-terminal", status: WorkspaceTaskStatus.Completed);
        await SeedReadyTaskAsync("task-backlog", status: WorkspaceTaskStatus.Backlog);
        await SeedReadyTaskAsync("task-nooptin", autoDispatch: false);
        await _store.EnqueueAsync(TaskIntent(1, "task-terminal"));
        await _store.EnqueueAsync(TaskIntent(2, "task-backlog"));
        await _store.EnqueueAsync(TaskIntent(3, "task-nooptin"));
        await _store.EnqueueAsync(TaskIntent(4, "task-missing"));

        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        Assert.IsEmpty(_evaluator.TaskCalls);
        Assert.IsEmpty(_starter.DetailedCalls);
        await using var db = await _factory.CreateDbContextAsync();
        var intents = await db.TaskSchedulerIntents.OrderBy(item => item.SourceEventId).ToListAsync();
        Assert.IsTrue(intents.All(item => item.Status == TaskSchedulerIntentStatuses.Done));
        var byTask = new Dictionary<string, (string? Outcome, string? Reason)>();
        foreach (var intent in intents)
        {
            var outcome = await _outcomeStore.GetOutcomeAsync(intent.IntentId);
            Assert.IsNotNull(outcome, $"intent {intent.SourceEventId} must have a durable outcome");
            byTask[intent.TaskId!] = (outcome.Outcome, outcome.ReasonCode);
        }

        Assert.AreEqual((TaskSchedulerIntentOutcomes.Terminal, "completed"), byTask["task-terminal"]);
        Assert.AreEqual((TaskSchedulerIntentOutcomes.Ineligible, "status_backlog"), byTask["task-backlog"]);
        Assert.AreEqual((TaskSchedulerIntentOutcomes.Ineligible, "not_opted_in"), byTask["task-nooptin"]);
        Assert.AreEqual((TaskSchedulerIntentOutcomes.Ineligible, "task_missing"), byTask["task-missing"]);
    }

    [TestMethod]
    public async Task Coordinator_DecisionPersistenceFailure_FailsClosed_NoStartNoDone()
    {
        await SeedReadyTaskAsync("task-1");
        await _store.EnqueueAsync(TaskIntent(1));
        var broken = new ThrowingDbContextFactory();

        await CreateCoordinator(maxAttempts: 3, brokenDecisionDbFactory: broken).ProcessOnceAsync();

        // fail closed：没有 durable decision 就不启动、不结算 outcome，intent 回 pending 等租约重试。
        Assert.IsEmpty(_starter.DetailedCalls);
        Assert.IsEmpty(await _outcomeStoreHasRows());
        await using var db = await _factory.CreateDbContextAsync();
        var intent = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Pending, intent.Status);
        Assert.IsNotNull(intent.LastError);
    }

    [TestMethod]
    public async Task Coordinator_CrashAfterOutcomeBeforeComplete_ReplayDoesNotDuplicateOutcome()
    {
        await SeedReadyTaskAsync("task-1");
        await _store.EnqueueAsync(TaskIntent(1));
        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        // 模拟 crash：outcome 已落库但 intent 停在 processing 且租约已过期。
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE task_scheduler_intents
                SET status = 'processing', lease_owner = 'dead-owner',
                    lease_until_utc = '2026-08-30T00:00:00.0000000Z'
                """);
        }

        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        await using var verify = await _factory.CreateDbContextAsync();
        var intent = await verify.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, intent.Status);
        // PK intent_id 幂等：重放不产生第二行，首轮 Assignment/Goal id 保留。
        var outcomes = await verify.Database
            .SqlQuery<string>($"""
                SELECT intent_id AS Value FROM task_scheduler_intent_outcomes
                """)
            .ToListAsync();
        Assert.HasCount(1, outcomes);
        var outcome = await _outcomeStore.GetOutcomeAsync(intent.IntentId);
        Assert.IsNotNull(outcome);
        Assert.AreEqual("asg-task-1", outcome.StartedAssignmentId);
    }

    [TestMethod]
    public async Task Coordinator_GoalTerminalIntent_RefreshesAvailabilityAndWritesNoopOutcome()
    {
        await _store.EnqueueAsync(GoalIntent(5));

        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        // 无 TaskId 的 goal 终态批：只刷新可用性（§5.3 步骤 1），无触发 Task → 不评估不派发。
        Assert.HasCount(0, _starter.DetailedCalls);
        await using var db = await _factory.CreateDbContextAsync();
        var intent = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, intent.Status);
        var outcome = await _outcomeStore.GetOutcomeAsync(intent.IntentId);
        Assert.IsNotNull(outcome);
        Assert.AreEqual(TaskSchedulerIntentOutcomes.Noop, outcome.Outcome);
        Assert.IsNull(outcome.TaskId);
        Assert.AreEqual("availability_refreshed", outcome.ReasonCode);
    }

    [TestMethod]
    public async Task Coordinator_PassesEffectiveMaxStartsOverrideFromOptions()
    {
        await SeedReadyTaskAsync("task-1");
        await _store.EnqueueAsync(TaskIntent(1));

        await CreateCoordinator(maxAttempts: 3, maxStartsPerScan: 3).ProcessOnceAsync();

        Assert.HasCount(1, _starter.DetailedCalls);
        Assert.AreEqual(3, _starter.DetailedCalls[0].maxStartsOverride);
    }

    [TestMethod]
    public async Task Coordinator_AuthoritativeSingleMode_EventPathStartsWithForcedMaxOne()
    {
        await SeedReadyTaskAsync("task-s1");
        await SeedReadyTaskAsync("task-s2");
        await _store.EnqueueAsync(TaskIntent(1, "task-s1"));
        await _store.EnqueueAsync(TaskIntent(2, "task-s2"));

        // authoritative-single（灰度试运行）：启动门控归一放行 + EffectiveMaxStartsPerScan 强制 1。
        await CreateCoordinator(maxAttempts: 3, maxStartsPerScan: 5, mode: "authoritative-single")
            .ProcessOnceAsync();

        Assert.HasCount(1, _starter.DetailedCalls);
        Assert.AreEqual(1, _starter.DetailedCalls[0].maxStartsOverride);
        await using var db = await _factory.CreateDbContextAsync();
        var intents = await db.TaskSchedulerIntents.OrderBy(item => item.SourceEventId).ToListAsync();
        Assert.HasCount(2, intents);
        Assert.IsTrue(intents.All(item => item.Status == TaskSchedulerIntentStatuses.Done));
        foreach (var intent in intents)
        {
            var outcome = await _outcomeStore.GetOutcomeAsync(intent.IntentId);
            Assert.IsNotNull(outcome);
            Assert.AreEqual(TaskSchedulerIntentOutcomes.Started, outcome.Outcome);
        }
    }

    [TestMethod]
    public async Task Coordinator_AuthoritativeBoundedMode_EventPathUsesConfiguredCap()
    {
        await SeedReadyTaskAsync("task-b1");
        await SeedReadyTaskAsync("task-b2");
        await _store.EnqueueAsync(TaskIntent(1, "task-b1"));
        await _store.EnqueueAsync(TaskIntent(2, "task-b2"));

        // authoritative-bounded（受限批量）：启动门控归一放行 + 上限用配置值（EffectiveMaxStartsPerScan=4）。
        await CreateCoordinator(maxAttempts: 3, maxStartsPerScan: 4, mode: "authoritative-bounded")
            .ProcessOnceAsync();

        Assert.HasCount(1, _starter.DetailedCalls);
        Assert.AreEqual(4, _starter.DetailedCalls[0].maxStartsOverride);
        await using var db = await _factory.CreateDbContextAsync();
        var intents = await db.TaskSchedulerIntents.OrderBy(item => item.SourceEventId).ToListAsync();
        Assert.HasCount(2, intents);
        Assert.IsTrue(intents.All(item => item.Status == TaskSchedulerIntentStatuses.Done));
    }

    [TestMethod]
    public async Task Coordinator_FailurePath_ParksIntentDeadWhenAttemptsExhausted()
    {
        await SeedReadyTaskAsync("task-3");
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
        await SeedReadyTaskAsync("task-4");
        await _store.EnqueueAsync(TaskIntent(4));
        // 模拟前一个协调者崩溃：intent 停在 processing 且租约已过期。
        var staleOwner = new TaskSchedulerIntentStore(_factory, NullLogger<TaskSchedulerIntentStore>.Instance);
        await staleOwner.DequeueAsync("ws", 10, TimeSpan.FromSeconds(1), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        await CreateCoordinator(maxAttempts: 3).ProcessOnceAsync();

        Assert.HasCount(1, _starter.DetailedCalls);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.TaskSchedulerIntents.SingleAsync();
        Assert.AreEqual(TaskSchedulerIntentStatuses.Done, row.Status);
    }

    [TestMethod]
    public async Task Coordinator_PausedWorkspace_DoesNotConsumeOrStart()
    {
        await _store.EnqueueAsync(TaskIntent(6));

        await CreateCoordinator(maxAttempts: 3, paused: true).ProcessOnceAsync();

        Assert.IsEmpty(_evaluator.TaskCalls);
        Assert.IsEmpty(_starter.DetailedCalls);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(
            TaskSchedulerIntentStatuses.Pending,
            (await db.TaskSchedulerIntents.SingleAsync()).Status);
    }

    private async Task<List<string>> _outcomeStoreHasRows()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Database
            .SqlQuery<string>($"""
                SELECT intent_id AS Value FROM task_scheduler_intent_outcomes
                """)
            .ToListAsync();
    }

    private sealed class FakeEvaluator : ITaskAutoDispatchEvaluator
    {
        public List<string> Calls { get; } = [];
        public List<IReadOnlyCollection<string>> TaskCalls { get; } = [];
        public IReadOnlyList<TaskAutoDispatchCandidateDecision> Return { get; set; } = [];
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
            string workspaceId,
            int limit,
            CancellationToken ct = default)
        {
            Calls.Add(workspaceId);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Return);
        }

        public Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateTasksAsync(
            string workspaceId,
            IReadOnlyCollection<string> taskIds,
            int candidateLimit,
            CancellationToken ct = default)
        {
            TaskCalls.Add(taskIds);
            if (Throw is not null)
                throw Throw;
            if (Return.Count > 0)
                return Task.FromResult(Return);
            // 默认对每个被评估的 taskId 生成一张完整字段的 Eligible 候选（围栏字段供断言）。
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions = taskIds
                .Select(id => new TaskAutoDispatchCandidateDecision
                {
                    WorkspaceId = workspaceId,
                    TaskId = id,
                    TaskVersion = 1,
                    AgentId = "agent-a",
                    ConversationId = "conv-a",
                    AgentRoutingFingerprint = "rfp",
                    ExecutionPlanFingerprint = "pfp",
                    AvailabilityVersion = 1,
                    Verdict = TaskAutoDispatchCandidateVerdict.Eligible,
                    Code = "eligible",
                    EvaluatedAtUtc = DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
                })
                .ToArray();
            return Task.FromResult(decisions);
        }
    }

    private sealed class FakeStarter : ITaskAutoDispatchStarter
    {
        public List<IReadOnlyList<TaskAutoDispatchCandidateDecision>> Calls { get; } = [];
        public List<(IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions, int? maxStartsOverride)> DetailedCalls { get; } = [];
        public Exception? Throw { get; set; }

        public async Task<int> DispatchAsync(
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
            int? maxStartsOverride = null,
            CancellationToken ct = default)
        {
            var outcomes = await DispatchDetailedAsync(decisions, maxStartsOverride, ct);
            return outcomes.Count(item => item.Started);
        }

        public Task<IReadOnlyList<TaskAutoDispatchStartOutcome>> DispatchDetailedAsync(
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
            int? maxStartsOverride = null,
            CancellationToken ct = default)
        {
            DetailedCalls.Add((decisions, maxStartsOverride));
            if (Throw is not null)
                throw Throw;
            IReadOnlyList<TaskAutoDispatchStartOutcome> outcomes = decisions
                .Where(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible)
                .Select(item => new TaskAutoDispatchStartOutcome
                {
                    TaskId = item.TaskId,
                    Started = true,
                    Code = TaskBoundGoalStartCodes.Started,
                    AgentId = item.AgentId,
                    AssignmentId = $"asg-{item.TaskId}",
                    GoalRunId = $"goal-{item.TaskId}",
                })
                .ToArray();
            return Task.FromResult(outcomes);
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
            => throw new InvalidOperationException("simulated decision db outage");

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("simulated decision db outage");
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
