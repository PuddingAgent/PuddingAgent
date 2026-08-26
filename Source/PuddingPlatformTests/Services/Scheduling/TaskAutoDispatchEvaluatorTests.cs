using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskAutoDispatchEvaluatorTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private TaskDependencyStore _dependencies = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-26T08:00:00Z");

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "auto-dispatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _dependencies = new TaskDependencyStore(_factory, new FixedTimeProvider(_now));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Evaluate_UsesDeterministicPriority_AndSelectsOneTaskPerAgent()
    {
        await AddTasksAsync(
            TaskEntity("p3", TaskPriority.P3, "agent-1"),
            TaskEntity("p0", TaskPriority.P0, "agent-1"));
        var evaluator = CreateEvaluator(Idle("agent-1"), new AllowWindow());

        var decisions = await evaluator.EvaluateAsync("ws", 20);

        Assert.AreEqual("p0", decisions[0].TaskId);
        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Eligible, decisions[0].Verdict);
        Assert.AreEqual("p3", decisions[1].TaskId);
        Assert.AreEqual("agent_already_selected_this_scan", decisions[1].Code);
        Assert.IsTrue(decisions.All(item => item.WorkspaceId == "ws"));
    }

    [TestMethod]
    public async Task Evaluate_WaitingSubAgent_IsNotEligible()
    {
        await AddTasksAsync(TaskEntity("task-1", TaskPriority.P1, "agent-1"));
        var availability = Idle("agent-1") with
        {
            State = AgentAvailabilityState.Busy,
            ActivityReason = AgentActivityReason.WaitingSubAgent,
            ActiveSubAgentRunId = "child-1",
            IdleSinceUtc = null,
            ReasonCode = "waiting_subagent",
        };
        var evaluator = CreateEvaluator(availability, new AllowWindow());

        var decision = AssertSingle(await evaluator.EvaluateAsync("ws", 20));

        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Deferred, decision.Verdict);
        Assert.AreEqual("agent_not_idle", decision.Code);
        Assert.AreEqual("waiting_subagent", decision.AvailabilityReason);
    }

    [TestMethod]
    public async Task Evaluate_OffPeakWithoutRouteProfile_FailsClosed()
    {
        var task = TaskEntity("task-1", TaskPriority.P1, "agent-1");
        task.ExecutionWindow = TaskExecutionWindow.OffPeakOnly;
        await AddTasksAsync(task);
        var evaluator = CreateEvaluator(
            Idle("agent-1"),
            new ConservativeExecutionWindowResolver());

        var decision = AssertSingle(await evaluator.EvaluateAsync("ws", 20));

        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Deferred, decision.Verdict);
        Assert.AreEqual("execution_window_unknown", decision.Code);
        Assert.AreEqual("execution_window_route_profile_unknown", decision.WindowCode);
    }

    [TestMethod]
    public void AuthoritativeMode_RequiresAllGoalSafetySwitches()
    {
        var errors = TaskAutoDispatchOptions.Validate(
            new TaskAutoDispatchOptions { Enabled = true, Mode = "authoritative" },
            new TaskBoundGoalOptions { Enabled = false },
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true });

        Assert.IsNotEmpty(errors);
        Assert.IsTrue(errors.Any(item => item.Contains("TaskBoundGoals:Enabled", StringComparison.Ordinal)));
    }

    private TaskAutoDispatchEvaluator CreateEvaluator(
        AgentAvailabilitySnapshot snapshot,
        IExecutionWindowResolver windowResolver) => new(
            _factory,
            _dependencies,
            new FixedAvailability(snapshot),
            windowResolver,
            Options.Create(new TaskAutoDispatchOptions
            {
                MinimumIdle = TimeSpan.FromMinutes(30),
            }),
            new FixedTimeProvider(_now));

    private async Task AddTasksAsync(params WorkspaceTaskEntity[] tasks)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.WorkspaceTasks.AddRange(tasks);
        await db.SaveChangesAsync();
    }

    private WorkspaceTaskEntity TaskEntity(
        string id,
        TaskPriority priority,
        string agentId) => new()
    {
        TaskId = id,
        WorkspaceId = "ws",
        Title = id,
        Status = WorkspaceTaskStatus.Ready,
        Priority = priority,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        PreferredAgentId = agentId,
        SortOrder = 0,
        Version = 1,
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private AgentAvailabilitySnapshot Idle(string agentId) => new()
    {
        WorkspaceId = "ws",
        AgentId = agentId,
        State = AgentAvailabilityState.Idle,
        ActivityReason = AgentActivityReason.None,
        Version = 7,
        ObservedAtUtc = _now,
        ValidUntilUtc = _now.AddMinutes(1),
        IdleSinceUtc = _now.AddHours(-1),
        ReasonCode = "idle_confirmed",
    };

    private static TaskAutoDispatchCandidateDecision AssertSingle(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions)
    {
        Assert.HasCount(1, decisions);
        return decisions[0];
    }

    private sealed class FixedAvailability(AgentAvailabilitySnapshot snapshot)
        : IAgentAvailabilityProjectionStore
    {
        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) => Task.FromResult(snapshot);

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) => Task.FromResult(snapshot);
    }

    private sealed class AllowWindow : IExecutionWindowResolver
    {
        public Task<ExecutionWindowDecision> EvaluateAsync(
            string workspaceId,
            string agentId,
            TaskExecutionWindow requestedWindow,
            DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult(new ExecutionWindowDecision
        {
            Verdict = ExecutionWindowVerdict.Allow,
            Code = "allowed_test",
            EvaluatedAtUtc = now,
            ValidUntilUtc = now.AddMinutes(1),
        });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
