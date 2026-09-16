using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// TaskGoalLaunchService 聚焦测试（设计 §8-2 全部 6 场景 + CAS/缺失/闸门补充）。
/// 直接构造被测服务（不依赖 DI 容器——组合根注册由父级后续波次追加）；
/// 开关联动链使用真实 <see cref="WorkspaceTaskAdminService"/>（SQLite in-memory），
/// 评估/派发使用 fake 记录调用。
/// </summary>
[TestClass]
public sealed class TaskGoalLaunchServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        DateTimeOffset.Parse("2026-09-16T08:00:00Z");

    private const string Ws = "ws-test";
    private const string TaskId = "task-1";
    private const string SelfAgent = "agent-a";
    private const string OtherAgent = "agent-b";

    // ── §8-2 场景①：跨 Agent 持有 assignment ⇒ task_held_by_other_agent ──

    [TestMethod]
    public async Task LaunchAsync_ActiveAssignmentHeldByOtherAgent_Refuses()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready, activeAssignmentId: "att-1", autoDispatchEnabled: false);
        SeedAttempt(harness.Db, "att-1", agentId: OtherAgent);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.HeldByOtherAgent, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount, "fail-closed：不得派发。");
        Assert.IsFalse(GetTask(harness.Db).AutoDispatchEnabled, "fail-closed：不得置开关。");
    }

    // ── §8-2 场景②：PreferredAgentId 指向他人 ⇒ 拒绝 ──

    [TestMethod]
    public async Task LaunchAsync_PreferredAgentPointsToOtherAgent_Refuses()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready, preferredAgentId: OtherAgent);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.PreferredAgentMismatch, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
    }

    // ── §8-2 场景③：调度器 shadow / paused ⇒ 拒绝（闸门在调用方，服务必须自检）──

    [TestMethod]
    public async Task LaunchAsync_SchedulerInShadowMode_RefusesAsNotAuthoritative()
    {
        await using var harness = await CreateHarnessAsync(options => options.Mode = "shadow");
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.SchedulerNotAuthoritative, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
        Assert.AreEqual(0, harness.Evaluator.CallCount, "拒绝发生在评估之前。");
    }

    [TestMethod]
    public async Task LaunchAsync_WorkspacePaused_RefusesAsPaused()
    {
        await using var harness = await CreateHarnessAsync(options => options.PausedWorkspaceIds = [Ws]);
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.SchedulerPaused, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
    }

    [TestMethod]
    public async Task LaunchAsync_SchedulerDisabled_RefusesAsDisabled()
    {
        await using var harness = await CreateHarnessAsync(options => options.Enabled = false);
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.SchedulerDisabled, result.Code);
    }

    // ── §8-2 场景④：非可派发状态 ⇒ 拒绝 ──

    [TestMethod]
    public async Task LaunchAsync_NonDispatchableStatus_Refuses()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Completed);

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.TaskNotDispatchable, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
    }

    // ── §8-2 场景⑤：已有活跃 Goal ⇒ 幂等返回既有 GoalRunId，不重复创建 ──

    [TestMethod]
    public async Task LaunchAsync_TaskAlreadyBoundToActiveGoal_ReturnsIdempotentHit()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        // Ready + active binding 是幂等检查服务的悬挂/竞态形态（§5-3 在 §5-8 之前）。
        // 开关 seed 为 false：幂等命中后断言仍为 false，证明幂等检查先于开关联动（§6-2 在 §6-3 之前）。
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready, autoDispatchEnabled: false);
        SeedBinding(harness.Db, goalRunId: "grun-existing");

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsTrue(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.AlreadyRunning, result.Code);
        Assert.AreEqual("grun-existing", result.GoalRunId);
        Assert.AreEqual(0, harness.Starter.CallCount, "幂等命中不得重复派发。");
        Assert.IsFalse(
            GetTask(harness.Db).AutoDispatchEnabled,
            "幂等检查先于开关联动（§6-2 在 §6-3 之前），不得置开关。");
    }

    // ── §8-2 场景⑥：成功路径 ⇒ 开关被置 true 且 DispatchDetailedAsync(maxStartsOverride:1) 恰一次 ──

    [TestMethod]
    public async Task LaunchAsync_SuccessPath_EnablesSwitchAndDispatchesSingleCardOnce()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        // 开关初始关闭（任务级 opt-in 的 canonical 场景）。
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready, autoDispatchEnabled: false);
        harness.Evaluator.Decisions = [CreateDecision()];
        harness.Starter.Outcomes = [CreateOutcome(started: true, goalRunId: "grun-new", assignmentId: "att-new")];

        var result = await harness.Service.LaunchAsync(
            CreateRequest(iterationBudget: 5)); // 请求 5 < 配置 32 ⇒ 生效 5（不抬高）

        Assert.IsTrue(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.Started, result.Code);
        Assert.AreEqual("grun-new", result.GoalRunId);
        Assert.AreEqual("att-new", result.AssignmentId);
        Assert.IsTrue(
            GetTask(harness.Db).AutoDispatchEnabled,
            "成功路径必须经 canonical 链置 auto_dispatch_enabled = true。");
        Assert.AreEqual(1, harness.Starter.CallCount, "DispatchDetailedAsync 恰好调用一次。");
        Assert.AreEqual(1, harness.Starter.LastMaxStartsOverride, "必须 maxStartsOverride: 1（只派这一张卡）。");
        var dispatched = harness.Starter.LastDecisions!;
        Assert.AreEqual(1, dispatched.Count);
        Assert.AreEqual(TaskId, dispatched[0].TaskId);
        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Eligible, dispatched[0].Verdict);
        StringAssert.Contains(result.Message, "5");
    }

    // ── 补充：§5-9 透传（评估非 Eligible ⇒ 透传决策码，不派发）──

    [TestMethod]
    public async Task LaunchAsync_EvaluationNotEligible_PassesThroughDecisionCode()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready);
        harness.Evaluator.Decisions =
        [
            CreateDecision(verdict: TaskAutoDispatchCandidateVerdict.Deferred, code: "agent_busy"),
        ];

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual("agent_busy", result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
        Assert.AreEqual(1, harness.Evaluator.CallCount);
        Assert.IsTrue(GetTask(harness.Db).AutoDispatchEnabled, "§6 序列：开关联动（步骤3）先于评估（步骤4）。");
    }

    // ── 补充：§5-1 / §5-2 ──

    [TestMethod]
    public async Task LaunchAsync_TaskMissing_ReturnsTaskNotFound()
    {
        await using var harness = await CreateHarnessAsync(_ => { });

        var result = await harness.Service.LaunchAsync(CreateRequest());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.TaskNotFound, result.Code);
        Assert.AreEqual(0, harness.Starter.CallCount);
    }

    [TestMethod]
    public async Task LaunchAsync_ExpectedVersionMismatch_ReturnsVersionConflict()
    {
        await using var harness = await CreateHarnessAsync(_ => { });
        SeedTask(harness.Db, status: WorkspaceTaskStatus.Ready, version: 3);

        var result = await harness.Service.LaunchAsync(CreateRequest(expectedVersion: 2));

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.VersionConflict, result.Code);
        Assert.AreEqual(3, result.TaskVersion);
        Assert.AreEqual(0, harness.Starter.CallCount);
    }

    // ── 基础设施 ──────────────────────────────────────────────

    private static TaskGoalLaunchRequest CreateRequest(
        int? expectedVersion = null,
        int? iterationBudget = null)
        => new()
        {
            WorkspaceId = Ws,
            TaskId = TaskId,
            AgentId = SelfAgent,
            ConversationId = "conv-1",
            ExpectedVersion = expectedVersion,
            IterationBudget = iterationBudget,
        };

    private static TaskAutoDispatchCandidateDecision CreateDecision(
        TaskAutoDispatchCandidateVerdict verdict = TaskAutoDispatchCandidateVerdict.Eligible,
        string code = "eligible",
        int? taskVersion = 3)
        => new()
        {
            WorkspaceId = Ws,
            TaskId = TaskId,
            TaskVersion = taskVersion,
            AgentId = SelfAgent,
            ConversationId = "conv-1",
            Verdict = verdict,
            Code = code,
            EvaluatedAtUtc = FixedNow,
        };

    private static TaskAutoDispatchStartOutcome CreateOutcome(
        bool started,
        string? goalRunId,
        string? assignmentId)
        => new()
        {
            TaskId = TaskId,
            Started = started,
            Code = started ? TaskGoalLaunchCodes.Started : "window_refused",
            AgentId = SelfAgent,
            AssignmentId = assignmentId,
            GoalRunId = goalRunId,
        };

    private static void SeedTask(
        PlatformDbContext db,
        WorkspaceTaskStatus status,
        string? preferredAgentId = null,
        string? activeAssignmentId = null,
        bool autoDispatchEnabled = true,
        int version = 3)
    {
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            WorkspaceId = Ws,
            TaskId = TaskId,
            Title = "被测任务",
            Status = status,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            PreferredAgentId = preferredAgentId,
            AutoDispatchEnabled = autoDispatchEnabled,
            ActiveAssignmentId = activeAssignmentId,
            Version = version,
            CreatedAtUtc = FixedNow,
            UpdatedAtUtc = FixedNow,
        });
        db.SaveChanges();
    }

    private static void SeedAttempt(PlatformDbContext db, string attemptId, string agentId)
    {
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = attemptId,
            WorkspaceId = Ws,
            TaskId = TaskId,
            AgentId = agentId,
            Status = AssignmentAttemptStatus.Assigned,
            CreatedAtUtc = FixedNow,
            UpdatedAtUtc = FixedNow,
        });
        db.SaveChanges();
    }

    private static void SeedBinding(PlatformDbContext db, string goalRunId)
    {
        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = $"binding-{goalRunId}",
            WorkspaceId = Ws,
            TaskId = TaskId,
            GoalRunId = goalRunId,
            AgentInstanceId = SelfAgent,
            Status = "active",
            CreatedAtUtc = FixedNow,
        });
        db.SaveChanges();
    }

    private static WorkspaceTaskEntity GetTask(PlatformDbContext db)
        => db.WorkspaceTasks.AsNoTracking().Single(item => item.WorkspaceId == Ws && item.TaskId == TaskId);

    private static async Task<TestHarness> CreateHarnessAsync(
        Action<TaskAutoDispatchOptions> configure)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var factory = new SingleConnectionDbContextFactory(connection);
        var schedulerOptions = new TaskAutoDispatchOptions
        {
            Enabled = true,
            Mode = "authoritative",
        };
        configure(schedulerOptions);
        var evaluator = new FakeEvaluator();
        var starter = new FakeStarter();
        var service = new TaskGoalLaunchService(
            factory,
            new WorkspaceTaskAdminService(factory),
            evaluator,
            starter,
            new FixedOptionsMonitor<TaskAutoDispatchOptions>(schedulerOptions),
            Options.Create(new TaskBoundGoalOptions { Enabled = true, GoalIterationBudget = 32 }),
            NullLogger<TaskGoalLaunchService>.Instance);

        return new TestHarness(connection, db, service, evaluator, starter);
    }

    private sealed record TestHarness(
        SqliteConnection Connection,
        PlatformDbContext Db,
        TaskGoalLaunchService Service,
        FakeEvaluator Evaluator,
        FakeStarter Starter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>共享一个 SQLite 连接、每次创建独立 context 的最小 factory
    /// （对齐 WorkspaceTaskAdminService 的「Singleton 服务 + 每调用独立 DbContext」消费模式）。</summary>
    private sealed class SingleConnectionDbContextFactory(SqliteConnection connection)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);

        public async Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => await Task.FromResult(CreateDbContext());
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeEvaluator : ITaskAutoDispatchEvaluator
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<TaskAutoDispatchCandidateDecision> Decisions { get; set; } = [];

        public Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
            string workspaceId,
            int limit,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Decisions);
        }

        public Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateTasksAsync(
            string workspaceId,
            IReadOnlyCollection<string> taskIds,
            int candidateLimit,
            CancellationToken ct = default)
            => throw new NotSupportedException("被测服务按设计 §6-4 只使用 EvaluateAsync。");
    }

    private sealed class FakeStarter : ITaskAutoDispatchStarter
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<TaskAutoDispatchCandidateDecision>? LastDecisions { get; private set; }

        public int? LastMaxStartsOverride { get; private set; }

        public IReadOnlyList<TaskAutoDispatchStartOutcome> Outcomes { get; set; } = [];

        public Task<int> DispatchAsync(
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
            int? maxStartsOverride = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("被测服务按设计 §6-5 只使用 DispatchDetailedAsync。");

        public Task<IReadOnlyList<TaskAutoDispatchStartOutcome>> DispatchDetailedAsync(
            IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
            int? maxStartsOverride = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastDecisions = decisions;
            LastMaxStartsOverride = maxStartsOverride;
            return Task.FromResult(Outcomes);
        }
    }
}
