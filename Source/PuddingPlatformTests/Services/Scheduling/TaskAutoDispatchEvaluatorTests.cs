using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
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
    public async Task Evaluate_RebuildsEachAgentOnceAndReusesOneFencedSnapshot()
    {
        await AddTasksAsync(
            TaskEntity("task-1", TaskPriority.P0, "agent-1"),
            TaskEntity("task-2", TaskPriority.P1, "agent-1"));
        var availability = new CountingAvailability(Idle("agent-1"));
        var evaluator = new TaskAutoDispatchEvaluator(
            _factory,
            _dependencies,
            availability,
            new AllowWindow(),
            new FixedAgentCatalog([Agent("agent-1")]),
            Options.Create(new TaskAutoDispatchOptions
            {
                MinimumIdle = TimeSpan.FromMinutes(30),
            }),
            new FixedTimeProvider(_now));

        var decisions = await evaluator.EvaluateAsync("ws", 20);

        Assert.HasCount(2, decisions);
        Assert.AreEqual(1, availability.RebuildCount);
        Assert.AreEqual(
            decisions[0].AvailabilityVersion,
            decisions[1].AvailabilityVersion,
            "Every candidate in one scan must reference the same per-Agent availability fence.");
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
    public async Task Evaluate_PreferredBusy_UsesExplicitCompatibleFallback()
    {
        var task = TaskEntity("task-1", TaskPriority.P0, "agent-1");
        task.AllowAgentFallback = true;
        task.RequiredCapabilitiesJson = "[\"cap-shell\"]";
        await AddTasksAsync(task);
        var busy = Idle("agent-1") with
        {
            State = AgentAvailabilityState.Busy,
            ActivityReason = AgentActivityReason.RuntimeExecution,
            IdleSinceUtc = null,
            ReasonCode = "foreground_turn",
        };
        var evaluator = CreateEvaluator(
            [busy, Idle("agent-2")],
            new AllowWindow(),
            [Agent("agent-1"), Agent("agent-2")]);

        var decision = AssertSingle(await evaluator.EvaluateAsync("ws", 20));

        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Eligible, decision.Verdict);
        Assert.AreEqual("agent-2", decision.AgentId);
        Assert.AreEqual("compatible_agent", decision.AgentSelectionCode);
        Assert.AreEqual(64, decision.AgentRoutingFingerprint!.Length);
    }

    [TestMethod]
    public async Task Evaluate_MissingRequiredCapability_FailsClosed()
    {
        var task = TaskEntity("task-1", TaskPriority.P0, "agent-1");
        task.RequiredCapabilitiesJson = "[\"cap-http-fetch\"]";
        await AddTasksAsync(task);
        var evaluator = CreateEvaluator(
            [Idle("agent-1")],
            new AllowWindow(),
            [Agent("agent-1") with { SelectedCapabilityIds = ["cap-shell", "cap-file-write"] }]);

        var decision = AssertSingle(await evaluator.EvaluateAsync("ws", 20));

        Assert.AreEqual(TaskAutoDispatchCandidateVerdict.Denied, decision.Verdict);
        Assert.AreEqual("preferred_agent_unavailable_or_incompatible", decision.Code);
    }

    [TestMethod]
    public async Task Evaluate_ReadyTaskWithoutExplicitAutoOptIn_IsNotACandidate()
    {
        var task = TaskEntity("task-1", TaskPriority.P0, "agent-1");
        task.AutoDispatchEnabled = false;
        await AddTasksAsync(task);
        var evaluator = CreateEvaluator(Idle("agent-1"), new AllowWindow());

        var decisions = await evaluator.EvaluateAsync("ws", 20);

        Assert.IsEmpty(decisions);
    }

    [TestMethod]
    public void RouteMatcher_TaskTypeRoleAndModelConstraints_AreFailClosed()
    {
        var task = TaskEntity("task-1", TaskPriority.P0, "agent-1");
        task.TaskType = "review";
        var roleMismatch = TaskAgentRouteMatcher.Evaluate(
            task,
            Agent("agent-1"),
            new TaskTypeRouteOptions { AllowedRoles = ["Audit"] });
        var modelMismatch = TaskAgentRouteMatcher.Evaluate(
            task,
            Agent("agent-1"),
            new TaskTypeRouteOptions { RequiredModelId = "deepseek-v4" });

        Assert.IsFalse(roleMismatch.Compatible);
        Assert.AreEqual("role_mismatch", roleMismatch.Code);
        Assert.IsFalse(modelMismatch.Compatible);
        Assert.AreEqual("model_mismatch", modelMismatch.Code);
    }

    [TestMethod]
    public void RouteMatcher_Fingerprint_IgnoresDtoProjectionTimestamps()
    {
        var task = TaskEntity("task-1", TaskPriority.P0, "agent-1");
        var original = Agent("agent-1");
        var reprojected = original with
        {
            CreatedAt = original.CreatedAt.AddYears(1),
            UpdatedAt = original.UpdatedAt.AddYears(1),
        };

        var first = TaskAgentRouteMatcher.Fingerprint(task, original);
        var second = TaskAgentRouteMatcher.Fingerprint(task, reprojected);

        Assert.AreEqual(first, second,
            "Projection timestamps are not routing facts and must not invalidate an atomic dispatch fence.");
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
        IExecutionWindowResolver windowResolver) => CreateEvaluator(
            [snapshot], windowResolver, [Agent(snapshot.AgentId)]);

    private TaskAutoDispatchEvaluator CreateEvaluator(
        IReadOnlyList<AgentAvailabilitySnapshot> snapshots,
        IExecutionWindowResolver windowResolver,
        IReadOnlyList<WorkspaceAgentDto> agents) => new(
            _factory,
            _dependencies,
            new FixedAvailability(snapshots),
            windowResolver,
            new FixedAgentCatalog(agents),
            Options.Create(new TaskAutoDispatchOptions
            {
                MinimumIdle = TimeSpan.FromMinutes(30),
            }),
            new FixedTimeProvider(_now));

    private WorkspaceAgentDto Agent(string agentId) => new(
        AgentId: agentId,
        Name: agentId,
        Description: null,
        DisplayName: agentId,
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "service",
        MainSessionId: $"conversation-{agentId}",
        SystemPromptOverride: null,
        PreferredProviderId: "bigmodel",
        PreferredModelId: "glm-5.3-flash",
        IsEnabled: true,
        IsFrozen: false,
        CreatedAt: _now.AddDays(-1),
        UpdatedAt: _now.AddDays(-1),
        Role: "Service",
        AllowFileWrite: true,
        AllowShellExecution: true,
        AllowNetworkAccess: true,
        SelectedCapabilityIds: ["cap-shell", "cap-file-write", "cap-http-fetch"]);

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
        TaskType = "implementation",
        AutoDispatchEnabled = true,
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

    private sealed class FixedAvailability(IReadOnlyList<AgentAvailabilitySnapshot> snapshots)
        : IAgentAvailabilityProjectionStore
    {
        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) => Task.FromResult(Find(agentId));

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) => Task.FromResult(Find(agentId));

        private AgentAvailabilitySnapshot Find(string agentId)
            => snapshots.Single(item => item.AgentId == agentId);
    }

    private sealed class CountingAvailability(AgentAvailabilitySnapshot snapshot)
        : IAgentAvailabilityProjectionStore
    {
        public int RebuildCount { get; private set; }

        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) => Task.FromResult(snapshot);

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
        {
            RebuildCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FixedAgentCatalog(IReadOnlyList<WorkspaceAgentDto> agents)
        : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => Task.FromResult(agents);
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
