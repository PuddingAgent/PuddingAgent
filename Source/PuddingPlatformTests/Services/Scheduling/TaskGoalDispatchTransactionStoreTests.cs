using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskGoalDispatchTransactionStoreTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private FixedTimeProvider _clock = null!;
    private WorkspaceAgentDto _agent = null!;
    private string _routingFingerprint = null!;
    private string _planFingerprint = null!;
    private TaskGoalDispatchTransactionStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-goal-start-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T08:00:00Z"));
        _agent = Agent();
        _store = new TaskGoalDispatchTransactionStore(
            _factory,
            new NoopSignal(),
            new GoalOutboxSignal(),
            _clock,
            new FixedAgentCatalog([_agent]),
            Options.Create(new TaskAutoDispatchOptions()),
            NullLogger<TaskGoalDispatchTransactionStore>.Instance);
        await SeedEligibleAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Start_CommitsTaskAssignmentReservationBindingGoalAndOutboxAtomically()
    {
        var result = await _store.StartAsync(Command());

        Assert.IsTrue(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.Started, result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync();
        var assignment = await db.TaskAssignmentAttempts.SingleAsync();
        var reservation = await db.AgentExecutionReservations.SingleAsync();
        var binding = await db.TaskGoalBindings.SingleAsync();
        var goal = await db.GoalRuns.SingleAsync();
        var outbox = await db.GoalOutbox.SingleAsync();
        var availability = await db.AgentAvailabilityProjections.SingleAsync();
        var plan = await db.TaskPlanRuns.SingleAsync();
        var nodes = await db.TaskNodes.OrderBy(item => item.SequenceNo).ToListAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, task.Status);
        Assert.AreEqual(3, task.Version);
        Assert.AreEqual(AssignmentAttemptStatus.Assigned, assignment.Status);
        Assert.AreEqual(task.ActiveAssignmentId, assignment.AttemptId);
        Assert.AreEqual("active", reservation.Status);
        Assert.IsGreaterThan(0, reservation.FencingToken);
        Assert.AreEqual(reservation.FencingToken, binding.ReservationFencingToken);
        Assert.AreEqual(task.Version, binding.ExpectedTaskVersion);
        Assert.AreEqual(goal.GoalRunId, binding.GoalRunId);
        Assert.AreEqual(plan.PlanId, binding.TaskPlanId);
        Assert.AreEqual(plan.PlanFingerprint, binding.PlanFingerprint);
        Assert.AreEqual(result.TaskPlanId, plan.PlanId);
        Assert.AreEqual(result.ExecutionPlanFingerprint, plan.PlanFingerprint);
        Assert.AreEqual(1, plan.WorkspaceTaskVersion);
        Assert.AreEqual(6, nodes.Count);
        Assert.AreEqual("Running", nodes[0].Status);
        CollectionAssert.AreEqual(
            new[] { "Explore", "Plan", "Change", "Test", "Review" },
            nodes.Skip(1).Select(item => item.WorkUnitKind).ToArray());
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual(32, goal.MaxIterations);
        Assert.AreEqual(GoalOutboxValues.Pending, outbox.Status);
        Assert.AreEqual(AgentAvailabilityState.Reserved, availability.State);
        Assert.AreEqual(goal.GoalRunId, availability.ActiveGoalRunId);
        Assert.AreEqual(2, await db.TaskEvents.CountAsync());
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.TaskGoalBound));
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.ContinuationRequested));
    }

    [TestMethod]
    public async Task SameIdempotencyKey_ReplaysWithoutDuplicateFacts()
    {
        var first = await _store.StartAsync(Command());
        var replay = await _store.StartAsync(Command());

        Assert.IsTrue(replay.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.IdempotentReplay, replay.Code);
        Assert.AreEqual(first.GoalRunId, replay.GoalRunId);
        Assert.AreEqual(first.ReservationFencingToken, replay.ReservationFencingToken);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());
        Assert.AreEqual(1, await db.TaskGoalBindings.CountAsync());
        Assert.AreEqual(1, await db.AgentExecutionReservations.CountAsync());
        Assert.AreEqual(1, await db.TaskPlanRuns.CountAsync());
        Assert.AreEqual(6, await db.TaskNodes.CountAsync());
    }

    [TestMethod]
    public async Task ChangedExecutionPlanFingerprint_FailsWithoutPartialWrites()
    {
        var result = await _store.StartAsync(Command() with
        {
            ExpectedExecutionPlanFingerprint = new string('c', 64),
        });

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.PlanChanged, result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.TaskPlanRuns.CountAsync());
        Assert.AreEqual(0, await db.TaskNodes.CountAsync());
        Assert.AreEqual(0, await db.TaskAssignmentAttempts.CountAsync());
        Assert.AreEqual(0, await db.AgentExecutionReservations.CountAsync());
        Assert.AreEqual(0, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task ChangedAgentRoutingFingerprint_FailsWithoutPartialWrites()
    {
        var result = await _store.StartAsync(Command() with
        {
            ExpectedAgentRoutingFingerprint = new string('b', 64),
        });

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.AgentChanged, result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.GoalRuns.CountAsync());
        Assert.AreEqual(0, await db.TaskAssignmentAttempts.CountAsync());
        Assert.AreEqual(0, await db.AgentExecutionReservations.CountAsync());
    }

    [TestMethod]
    public async Task ConcurrentSameStart_HasOneFactSetAndNoUnhandledWriterRace()
    {
        var secondStore = new TaskGoalDispatchTransactionStore(
            _factory,
            new NoopSignal(),
            new GoalOutboxSignal(),
            _clock,
            new FixedAgentCatalog([_agent]),
            Options.Create(new TaskAutoDispatchOptions()),
            NullLogger<TaskGoalDispatchTransactionStore>.Instance);

        var results = await System.Threading.Tasks.Task.WhenAll(
            _store.StartAsync(Command()),
            secondStore.StartAsync(Command()));

        Assert.AreEqual(1, results.Count(item => item.Code == TaskBoundGoalStartCodes.Started));
        Assert.IsTrue(results.All(item => item.Started
            || item.Code == TaskBoundGoalStartCodes.LostRace));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());
        Assert.AreEqual(1, await db.TaskGoalBindings.CountAsync());
        Assert.AreEqual(1, await db.TaskAssignmentAttempts.CountAsync());
    }

    [TestMethod]
    public async Task ChangedTaskVersion_FailsWithoutPartialWrites()
    {
        var result = await _store.StartAsync(Command() with { ExpectedTaskVersion = 2 });

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.TaskChanged, result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.TaskAssignmentAttempts.CountAsync());
        Assert.AreEqual(0, await db.AgentExecutionReservations.CountAsync());
        Assert.AreEqual(0, await db.GoalRuns.CountAsync());
        Assert.AreEqual(WorkspaceTaskStatus.Ready, (await db.WorkspaceTasks.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task ChangedAvailabilityVersion_FailsClosed()
    {
        var result = await _store.StartAsync(Command() with { ExpectedAvailabilityVersion = 8 });

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.AgentChanged, result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task HistoricalTerminalAssignment_DoesNotBlockAtomicStartForAgent()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.WorkspaceTasks.Add(new WorkspaceTaskEntity
            {
                TaskId = "completed-task",
                WorkspaceId = "ws",
                Title = "historical completed task",
                Status = WorkspaceTaskStatus.Completed,
                Priority = TaskPriority.P3,
                ExecutionWindow = TaskExecutionWindow.Anytime,
                ActiveAssignmentId = "historical-assignment",
                SortOrder = 0,
                Version = 2,
                CreatedAtUtc = _clock.GetUtcNow().AddDays(-1),
                UpdatedAtUtc = _clock.GetUtcNow().AddDays(-1),
                CompletedAtUtc = _clock.GetUtcNow().AddDays(-1),
            });
            db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
            {
                AttemptId = "historical-assignment",
                TaskId = "completed-task",
                WorkspaceId = "ws",
                AgentId = "agent-1",
                AttemptNumber = 1,
                Status = AssignmentAttemptStatus.InProgress,
                CreatedAtUtc = _clock.GetUtcNow().AddDays(-1),
                UpdatedAtUtc = _clock.GetUtcNow().AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var result = await _store.StartAsync(Command());

        Assert.IsTrue(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.Started, result.Code);
    }

    [TestMethod]
    public async Task DetachedBlockedTaskGoalFromPriorTask_IsRetiredBeforeFreshDispatchStarts()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var now = _clock.GetUtcNow().AddMinutes(-5);
            db.GoalRuns.Add(new GoalRunEntity
            {
                GoalRunId = "blocked-goal",
                WorkspaceId = "ws",
                CurrentConversationId = "conversation-1",
                AgentInstanceId = "agent-1",
                Objective = "previous task attempt",
                Status = GoalPhase.Blocked,
                BlockedCode = "iteration_failed",
                BlockedMessage = "previous attempt failed",
                SourceCommandId = "old-task-goal-command",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            db.TaskGoalBindings.Add(new TaskGoalBindingEntity
            {
                BindingId = "terminal-binding",
                WorkspaceId = "ws",
                TaskId = "previous-task",
                ExpectedTaskVersion = 1,
                GoalRunId = "blocked-goal",
                AgentInstanceId = "agent-1",
                Status = "terminal",
                IdempotencyKey = "task-goal:ws:task-1:old",
                CreatedAtUtc = now,
                ReleasedAtUtc = now.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        }

        var result = await _store.StartAsync(Command());

        Assert.IsTrue(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.Started, result.Code);
        await using var verify = await _factory.CreateDbContextAsync();
        var oldGoal = await verify.GoalRuns.SingleAsync(item => item.GoalRunId == "blocked-goal");
        Assert.AreEqual(GoalPhase.Failed, oldGoal.Status);
        Assert.AreEqual("superseded_by_task_retry", oldGoal.StatusReason);
        Assert.IsNotNull(oldGoal.TerminalAtUtc);
        Assert.AreEqual(1, await verify.GoalRuns.CountAsync(item => item.Status == GoalPhase.Active));
        Assert.AreEqual(1, await verify.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.Failed
                && item.EventId.StartsWith("tgr-blocked-goal-")));
    }

    [TestMethod]
    public async Task DependencyChangedAfterEvaluation_FailsClosed()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.WorkspaceTasks.Add(new WorkspaceTaskEntity
            {
                TaskId = "predecessor",
                WorkspaceId = "ws",
                Title = "predecessor",
                Status = WorkspaceTaskStatus.InProgress,
                Priority = TaskPriority.P1,
                ExecutionWindow = TaskExecutionWindow.Anytime,
                SortOrder = 0,
                Version = 1,
                CreatedAtUtc = _clock.GetUtcNow(),
                UpdatedAtUtc = _clock.GetUtcNow(),
            });
            db.TaskDependencies.Add(new TaskDependencyEntity
            {
                DependencyId = "dependency-1",
                WorkspaceId = "ws",
                PredecessorTaskId = "predecessor",
                SuccessorTaskId = "task-1",
                Kind = "finish_to_start",
                CreatedAtUtc = _clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        var result = await _store.StartAsync(Command());

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskBoundGoalStartCodes.DependencyChanged, result.Code);
    }

    [TestMethod]
    public async Task Acceptance_RechecksTaskVersionAndReservationFence()
    {
        var started = await _store.StartAsync(Command());
        var outboxStore = new GoalOutboxStore(_factory);
        var due = (await outboxStore.PeekDueAsync(_clock.GetUtcNow(), 1)).Single();
        var lease = (await outboxStore.TryClaimAsync(
            due.OutboxId, "worker", _clock.GetUtcNow(), TimeSpan.FromMinutes(2)))!;

        await using (var mutate = await _factory.CreateDbContextAsync())
        {
            var task = await mutate.WorkspaceTasks.SingleAsync();
            task.Version++;
            await mutate.SaveChangesAsync();
        }

        await using var db = await _factory.CreateDbContextAsync();
        var acceptance = new ConversationAcceptanceStore(
            db,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance,
            taskBoundOptions: null,
            timeProvider: _clock);
        var error = await Assert.ThrowsAsync<GoalContinuationAcceptanceException>(() =>
            acceptance.AcceptBatchAsync(
                ContinuationRequest(started, lease),
                "ws", "conversation-1", null, CancellationToken.None));
        Assert.AreEqual(GoalContinuationAcceptanceErrorCodes.TaskFenceChanged, error.Code);
        Assert.AreEqual(0, await db.GoalIterations.CountAsync());
    }

    private async Task SeedEligibleAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var task = new WorkspaceTaskEntity
        {
            TaskId = "task-1",
            WorkspaceId = "ws",
            Title = "Implement bounded automatic work",
            Description = "Do one task at a time.",
            AcceptanceCriteria = "Canonical task completion evidence exists.",
            Status = WorkspaceTaskStatus.Ready,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            PreferredAgentId = "agent-1",
            TaskType = "implementation",
            AutoDispatchEnabled = true,
            SortOrder = 0,
            Version = 1,
            CreatedAtUtc = _clock.GetUtcNow(),
            UpdatedAtUtc = _clock.GetUtcNow(),
        };
        _routingFingerprint = TaskAgentRouteMatcher.Fingerprint(task, _agent);
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(task, null, out var plan, out _));
        _planFingerprint = plan!.Fingerprint;
        db.WorkspaceTasks.Add(task);
        db.AgentAvailabilityProjections.Add(new AgentAvailabilityProjectionEntity
        {
            WorkspaceId = "ws",
            AgentId = "agent-1",
            State = AgentAvailabilityState.Idle,
            ActivityReason = AgentActivityReason.None,
            Version = 7,
            ObservedAtUtc = _clock.GetUtcNow(),
            ValidUntilUtc = _clock.GetUtcNow().AddMinutes(1),
            IdleSinceUtc = _clock.GetUtcNow().AddHours(-1),
            MainConversationId = "conversation-1",
            ReasonCode = "idle_confirmed",
        });
        await db.SaveChangesAsync();
    }

    private StartGoalFromTaskCommand Command() => new()
    {
        WorkspaceId = "ws",
        TaskId = "task-1",
        ExpectedTaskVersion = 1,
        AgentId = "agent-1",
        ExpectedAgentRoutingFingerprint = _routingFingerprint,
        ExpectedExecutionPlanFingerprint = _planFingerprint,
        ConversationId = "conversation-1",
        ExpectedAvailabilityVersion = 7,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        WindowDecision = new ExecutionWindowDecision
        {
            Verdict = ExecutionWindowVerdict.Allow,
            Code = "allowed_test",
            EvaluatedAtUtc = _clock.GetUtcNow(),
            ValidUntilUtc = _clock.GetUtcNow().AddMinutes(1),
        },
        GoalIterationBudget = 32,
        MinimumIdle = TimeSpan.FromMinutes(30),
        ReservationLease = TimeSpan.FromHours(2),
        RequestedAtUtc = _clock.GetUtcNow(),
        OwnerId = "coordinator-1",
        CausationId = "scan-1",
        CorrelationId = "task-1",
        IdempotencyKey = "task-goal:ws:task-1:1",
    };

    private static SubmitTurnRequest ContinuationRequest(
        TaskBoundGoalStartResult started,
        GoalOutboxEntity lease) => new()
    {
        ClientRequestId = lease.OutboxId,
        ClientMessageId = $"message-{lease.OutboxId}",
        Recipients = new RecipientRequest { Type = "agent", AgentIds = ["agent-1"] },
        Content = [new ContentPart { Type = "text", Text = "continue" }],
        GoalContinuation = new GoalContinuationAcceptanceContext
        {
            OutboxId = lease.OutboxId,
            GoalRunId = started.GoalRunId!,
            ActivationEpoch = 1,
            AggregateVersion = 1,
            IterationNo = 1,
            LeaseOwner = lease.LeaseOwner!,
            FencingToken = lease.FencingToken,
            TaskId = "task-1",
            ExpectedTaskVersion = 3,
            ReservationFencingToken = started.ReservationFencingToken,
        },
    };

    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);
        public void Signal(string conversationId, long committedThroughSequence)
        {
        }
    }

    private WorkspaceAgentDto Agent() => new(
        AgentId: "agent-1",
        Name: "agent-1",
        Description: null,
        DisplayName: "Agent 1",
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "service",
        MainSessionId: "conversation-1",
        SystemPromptOverride: null,
        PreferredProviderId: "bigmodel",
        PreferredModelId: "glm-5.3-flash",
        IsEnabled: true,
        IsFrozen: false,
        CreatedAt: _clock.GetUtcNow().AddDays(-1),
        UpdatedAt: _clock.GetUtcNow().AddDays(-1),
        Role: "Service",
        AllowFileWrite: true,
        AllowShellExecution: true,
        AllowNetworkAccess: true,
        SelectedCapabilityIds: ["code"]);

    private sealed class FixedAgentCatalog(IReadOnlyList<WorkspaceAgentDto> agents)
        : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => Task.FromResult(agents);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
