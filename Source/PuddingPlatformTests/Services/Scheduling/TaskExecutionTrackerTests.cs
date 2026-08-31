using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskExecutionTrackerTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-28T08:00:00Z");

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-execution-tracker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task PendingContinuation_IsWaitingAndReadOnly()
    {
        await SeedAsync();

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Waiting, decision.Verdict);
        Assert.AreEqual("continuation_pending", decision.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, (await db.WorkspaceTasks.SingleAsync()).Status);
        Assert.AreEqual("pending", (await db.GoalOutbox.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task ExpiredReservation_IsStalled()
    {
        await SeedAsync(reservationLeaseUntil: _now.AddSeconds(-1));

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Stalled, decision.Verdict);
        Assert.AreEqual("reservation_expired", decision.Code);
    }

    [TestMethod]
    public async Task TerminalCommandWithOpenIteration_IsStalled()
    {
        await SeedAsync(addRunningIteration: true, commandStatus: "failed");

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Stalled, decision.Verdict);
        Assert.AreEqual("command_terminal_iteration_open", decision.Code);
        Assert.AreEqual("failed", decision.CommandStatus);
    }

    [TestMethod]
    public async Task AssignmentFenceMismatch_IsInconsistent()
    {
        await SeedAsync(activeAssignmentId: "other-assignment");

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Inconsistent, decision.Verdict);
        Assert.AreEqual("assignment_fence_mismatch", decision.Code);
    }

    [TestMethod]
    public async Task TerminalTaskWithActiveBinding_RequiresCleanup()
    {
        await SeedAsync(taskStatus: WorkspaceTaskStatus.Completed);

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.CleanupRequired, decision.Verdict);
        Assert.AreEqual("terminal_binding_still_active", decision.Code);
    }

    [TestMethod]
    public async Task BoundExecutionPlan_ProjectsCurrentWorkUnit()
    {
        await SeedAsync();
        await AddPlanAsync("fingerprint-1");

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Waiting, decision.Verdict);
        Assert.AreEqual("plan-1", decision.TaskPlanId);
        Assert.AreEqual("fingerprint-1", decision.ExecutionPlanFingerprint);
        Assert.AreEqual("Active", decision.ExecutionPlanStatus);
        Assert.AreEqual("Explore", decision.WorkUnitKind);
        Assert.AreEqual("Planned", decision.WorkUnitStatus);
    }

    [TestMethod]
    public async Task MissingBoundExecutionPlan_IsInconsistent()
    {
        await SeedAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var binding = await db.TaskGoalBindings.SingleAsync();
            binding.TaskPlanId = "missing-plan";
            binding.PlanFingerprint = "fingerprint-1";
            await db.SaveChangesAsync();
        }

        var decision = AssertSingle(await CreateTracker().EvaluateAsync("ws", 10));

        Assert.AreEqual(TaskExecutionTrackingVerdict.Inconsistent, decision.Verdict);
        Assert.AreEqual("execution_plan_missing", decision.Code);
    }

    [TestMethod]
    public async Task RepairCoordinator_CleansTerminalBindingAndAssignment()
    {
        await SeedAsync(taskStatus: WorkspaceTaskStatus.Completed);
        var decisions = await CreateTracker().EvaluateAsync("ws", 10);

        var summary = await CreateRepairCoordinator().RepairAsync("ws", decisions);

        Assert.AreEqual(1, summary.Repaired);
        Assert.AreEqual(1, summary.RepairedByCode["terminal_binding_still_active"]);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual("terminal", (await db.TaskGoalBindings.SingleAsync()).Status);
        Assert.IsNull((await db.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
        Assert.AreEqual(AssignmentAttemptStatus.Completed,
            (await db.TaskAssignmentAttempts.SingleAsync()).Status);
        Assert.IsNotNull((await db.TaskAssignmentAttempts.SingleAsync()).ReleasedAtUtc);
    }

    [TestMethod]
    public async Task RepairCoordinator_CleansBlockedGoalBindingAndReleasesAgentOwnership()
    {
        await SeedAsync(
            taskStatus: WorkspaceTaskStatus.Blocked,
            goalStatus: GoalPhase.Blocked);
        // Mirrors the historical settlement bug: Reservation was released, while
        // Binding + Assignment stayed active and permanently occupied the Agent.
        await using (var mutate = await _factory.CreateDbContextAsync())
        {
            var reservation = await mutate.AgentExecutionReservations.SingleAsync();
            reservation.Status = "released";
            reservation.ReleaseReason = "goal_blocked";
            reservation.ReleasedAtUtc = _now.AddMinutes(-1);
            reservation.UpdatedAtUtc = _now.AddMinutes(-1);
            await mutate.SaveChangesAsync();
        }
        var decisions = await CreateTracker().EvaluateAsync("ws", 10);
        var decision = AssertSingle(decisions);
        Assert.AreEqual(TaskExecutionTrackingVerdict.CleanupRequired, decision.Verdict);
        Assert.AreEqual("blocked_binding_still_active", decision.Code);

        var summary = await CreateRepairCoordinator().RepairAsync("ws", decisions);

        Assert.AreEqual(1, summary.Repaired);
        Assert.AreEqual(1, summary.RepairedByCode["blocked_binding_still_active"]);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, (await db.WorkspaceTasks.SingleAsync()).Status);
        Assert.IsNull((await db.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
        Assert.AreEqual("terminal", (await db.TaskGoalBindings.SingleAsync()).Status);
        Assert.AreEqual(AssignmentAttemptStatus.Failed,
            (await db.TaskAssignmentAttempts.SingleAsync()).Status);
        Assert.AreEqual("released", (await db.AgentExecutionReservations.SingleAsync()).Status);
        Assert.AreEqual("goal_blocked",
            (await db.AgentExecutionReservations.SingleAsync()).ReleaseReason);
    }

    [TestMethod]
    public async Task RepairCoordinator_ReleasesDeliveredLegacyAssignmentWithoutExecutionClaim()
    {
        await SeedAsync(taskStatus: WorkspaceTaskStatus.InProgress);
        await using (var mutate = await _factory.CreateDbContextAsync())
        {
            mutate.TaskGoalBindings.RemoveRange(mutate.TaskGoalBindings);
            mutate.AgentExecutionReservations.RemoveRange(mutate.AgentExecutionReservations);
            mutate.GoalOutbox.RemoveRange(mutate.GoalOutbox);
            mutate.GoalRuns.RemoveRange(mutate.GoalRuns);
            var task = await mutate.WorkspaceTasks.SingleAsync();
            var assignment = await mutate.TaskAssignmentAttempts.SingleAsync();
            var stale = _now.AddHours(-1);
            task.UpdatedAtUtc = stale;
            assignment.UpdatedAtUtc = stale;
            mutate.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
            {
                TaskId = task.TaskId,
                AssignmentId = assignment.AttemptId,
                DeliveryId = "delivery-1",
                BoundAtUtc = stale,
            });
            mutate.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = "delivery-1",
                MessageId = "message-legacy-1",
                WorkspaceId = "ws",
                TargetKind = "agent",
                TargetId = assignment.AgentId,
                Status = "delivered",
                HandlingMode = "execute",
                CreatedAt = stale.ToUnixTimeMilliseconds(),
                UpdatedAt = stale.ToUnixTimeMilliseconds(),
                AckAt = stale.ToUnixTimeMilliseconds(),
            });
            await mutate.SaveChangesAsync();
        }

        var decisions = await CreateTracker().EvaluateAsync("ws", 10);
        var decision = AssertSingle(decisions);
        Assert.AreEqual(TaskExecutionTrackingVerdict.CleanupRequired, decision.Verdict);
        Assert.AreEqual("legacy_assignment_execution_missing", decision.Code);

        var summary = await CreateRepairCoordinator().RepairAsync("ws", decisions);

        Assert.AreEqual(1, summary.Repaired);
        await using var verify = await _factory.CreateDbContextAsync();
        var taskAfter = await verify.WorkspaceTasks.SingleAsync();
        var assignmentAfter = await verify.TaskAssignmentAttempts.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, taskAfter.Status);
        Assert.AreEqual("assignment_execution_missing", taskAfter.BlockerKind);
        Assert.IsNull(taskAfter.ActiveAssignmentId);
        Assert.AreEqual(AssignmentAttemptStatus.Failed, assignmentAfter.Status);
        Assert.IsNotNull(assignmentAfter.ReleasedAtUtc);
        Assert.AreEqual(1, await verify.TaskEvents.CountAsync(
            item => item.EventType == TaskEventType.TaskBlocked));
    }

    [TestMethod]
    public async Task RepairCoordinator_ReleasesTerminalLegacyDeliveryWithoutWaitingForThreshold()
    {
        await SeedAsync(taskStatus: WorkspaceTaskStatus.InProgress);
        await using (var mutate = await _factory.CreateDbContextAsync())
        {
            mutate.TaskGoalBindings.RemoveRange(mutate.TaskGoalBindings);
            mutate.AgentExecutionReservations.RemoveRange(mutate.AgentExecutionReservations);
            mutate.GoalOutbox.RemoveRange(mutate.GoalOutbox);
            mutate.GoalRuns.RemoveRange(mutate.GoalRuns);
            var task = await mutate.WorkspaceTasks.SingleAsync();
            var assignment = await mutate.TaskAssignmentAttempts.SingleAsync();
            var recent = _now.AddMinutes(-1);
            task.UpdatedAtUtc = recent;
            assignment.UpdatedAtUtc = recent;
            mutate.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
            {
                TaskId = task.TaskId,
                AssignmentId = assignment.AttemptId,
                DeliveryId = "delivery-dead-1",
                BoundAtUtc = recent,
            });
            mutate.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = "delivery-dead-1",
                MessageId = "message-dead-1",
                WorkspaceId = "ws",
                TargetKind = "agent",
                TargetId = assignment.AgentId,
                Status = "dead_letter",
                HandlingMode = "execute",
                CreatedAt = recent.ToUnixTimeMilliseconds(),
                UpdatedAt = recent.ToUnixTimeMilliseconds(),
            });
            await mutate.SaveChangesAsync();
        }

        var decisions = await CreateTracker().EvaluateAsync("ws", 10);
        var decision = AssertSingle(decisions);
        Assert.AreEqual(TaskExecutionTrackingVerdict.CleanupRequired, decision.Verdict);
        Assert.AreEqual("legacy_delivery_terminal_without_execution", decision.Code);

        var summary = await CreateRepairCoordinator().RepairAsync("ws", decisions);

        Assert.AreEqual(1, summary.Repaired);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, (await verify.WorkspaceTasks.SingleAsync()).Status);
        Assert.IsNull((await verify.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
        Assert.AreEqual(AssignmentAttemptStatus.Failed,
            (await verify.TaskAssignmentAttempts.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task RepairCoordinator_RecoversExpiredContinuationLease()
    {
        await SeedAsync();
        await using (var mutate = await _factory.CreateDbContextAsync())
        {
            var outbox = await mutate.GoalOutbox.SingleAsync();
            outbox.Status = GoalOutboxValues.Leased;
            outbox.LeaseOwner = "dead-worker";
            outbox.LeaseUntilUtc = _now.AddMinutes(-1);
            await mutate.SaveChangesAsync();
        }
        var decisions = await CreateTracker().EvaluateAsync("ws", 10);
        Assert.AreEqual("continuation_lease_expired", decisions.Single().Code);

        var summary = await CreateRepairCoordinator().RepairAsync("ws", decisions);

        Assert.AreEqual(1, summary.Repaired);
        await using var db = await _factory.CreateDbContextAsync();
        var repaired = await db.GoalOutbox.SingleAsync();
        Assert.AreEqual(GoalOutboxValues.Pending, repaired.Status);
        Assert.IsNull(repaired.LeaseOwner);
        Assert.IsNull(repaired.LeaseUntilUtc);
        Assert.AreEqual("tracker_recovered_expired_lease", repaired.LastError);
    }

    private TaskExecutionTracker CreateTracker() => new(
        _factory,
        Options.Create(new TaskAutoDispatchOptions
        {
            TrackerStallThreshold = TimeSpan.FromMinutes(30),
        }),
        new FixedTimeProvider(_now));

    private TaskExecutionRepairCoordinator CreateRepairCoordinator() => new(
        _factory,
        new GoalOutboxSignal(),
        Options.Create(new TaskAutoDispatchOptions
        {
            TrackerStallThreshold = TimeSpan.FromMinutes(30),
        }),
        new FixedTimeProvider(_now),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskExecutionRepairCoordinator>.Instance);

    private async Task SeedAsync(
        DateTimeOffset? reservationLeaseUntil = null,
        bool addRunningIteration = false,
        string commandStatus = "running",
        string activeAssignmentId = "assignment-1",
        WorkspaceTaskStatus taskStatus = WorkspaceTaskStatus.Assigned,
        GoalPhase goalStatus = GoalPhase.Active)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var created = _now.AddMinutes(-5);
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = "task-1",
            WorkspaceId = "ws",
            Title = "tracked task",
            Description = "tracked task",
            AcceptanceCriteria = "canonical terminal evidence",
            Status = taskStatus,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            AutoDispatchEnabled = true,
            ActiveAssignmentId = activeAssignmentId,
            Version = 2,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = "assignment-1",
            TaskId = "task-1",
            WorkspaceId = "ws",
            AgentId = "agent-1",
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.Assigned,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            ActiveAtUtc = created,
        });
        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = "goal-1",
            WorkspaceId = "ws",
            CurrentConversationId = "conversation-1",
            AgentInstanceId = "agent-1",
            Objective = "tracked task",
            Status = goalStatus,
            MaxIterations = 32,
            ActivationEpoch = 1,
            AggregateVersion = 1,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        });
        var reservation = new AgentExecutionReservationEntity
        {
            ReservationId = "reservation-1",
            WorkspaceId = "ws",
            AgentId = "agent-1",
            TaskId = "task-1",
            GoalRunId = "goal-1",
            OwnerId = "scheduler",
            Status = "active",
            LeaseUntilUtc = reservationLeaseUntil ?? _now.AddHours(1),
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        };
        db.AgentExecutionReservations.Add(reservation);
        var binding = new TaskGoalBindingEntity
        {
            BindingId = "binding-1",
            WorkspaceId = "ws",
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            ExpectedTaskVersion = 2,
            GoalRunId = "goal-1",
            AgentInstanceId = "agent-1",
            ReservationId = "reservation-1",
            Status = "active",
            IdempotencyKey = "task-goal:ws:task-1:1",
            CreatedAtUtc = created,
        };
        db.TaskGoalBindings.Add(binding);
        db.GoalOutbox.Add(new GoalOutboxEntity
        {
            OutboxId = "outbox-1",
            GoalRunId = "goal-1",
            ActivationEpoch = 1,
            AggregateVersion = 1,
            Kind = GoalOutboxValues.Continuation,
            IdempotencyKey = "goal-1:1:1",
            Status = GoalOutboxValues.Pending,
            DueAtUtc = created,
            CreatedAtUtc = created,
        });
        if (addRunningIteration)
        {
            db.GoalIterations.Add(new GoalIterationEntity
            {
                GoalIterationId = "iteration-1",
                GoalRunId = "goal-1",
                ActivationEpoch = 1,
                IterationNo = 1,
                Status = "running",
                CommandId = "command-1",
                TurnId = "turn-1",
                StartedAtUtc = created,
                CreatedAtUtc = created,
            });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command-1",
                BatchId = "batch-1",
                WorkspaceId = "ws",
                SessionId = "conversation-1",
                UserMessageId = "message-1",
                TurnId = "turn-1",
                AgentInstanceId = "agent-1",
                Status = commandStatus,
                CreatedAt = created.ToUnixTimeMilliseconds(),
            });
        }
        await db.SaveChangesAsync();
        binding.ReservationFencingToken = reservation.FencingToken;
        await db.SaveChangesAsync();
    }

    private async Task AddPlanAsync(string fingerprint)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var binding = await db.TaskGoalBindings.SingleAsync();
        binding.TaskPlanId = "plan-1";
        binding.PlanFingerprint = fingerprint;
        var nowMs = _now.AddMinutes(-4).ToUnixTimeMilliseconds();
        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = "plan-1",
            WorkspaceId = "ws",
            WorkspaceTaskId = "task-1",
            WorkspaceTaskVersion = 1,
            PlanVersion = 1,
            SchemaVersion = 1,
            PlanKind = "workspace-task-v1",
            PlanFingerprint = fingerprint,
            RootSessionId = "conversation-1",
            LeaderAgentId = "agent-1",
            Status = "Active",
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
        });
        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = "node-explore",
            PlanId = "plan-1",
            ParentTaskNodeId = "node-root",
            Depth = 1,
            SequenceNo = 1,
            WorkUnitKind = "Explore",
            AssignedToKind = "Leader",
            Status = "Planned",
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
        });
        await db.SaveChangesAsync();
    }

    private static TaskExecutionTrackingDecision AssertSingle(
        IReadOnlyList<TaskExecutionTrackingDecision> decisions)
    {
        Assert.HasCount(1, decisions);
        return decisions[0];
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
