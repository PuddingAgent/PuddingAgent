using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Bounded authoritative repairs for facts whose desired state is deterministic.
/// Unknown/inconsistent states remain observations; this component never guesses
/// task success, renews an expired execution reservation, or synthesizes a Turn.
/// </summary>
public sealed class TaskExecutionRepairCoordinator(
    IDbContextFactory<PlatformDbContext> dbFactory,
    GoalOutboxSignal outboxSignal,
    IOptions<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<TaskExecutionRepairCoordinator> logger) : ITaskExecutionRepairCoordinator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TaskAutoDispatchOptions _options = options.Value;

    public async Task<TaskExecutionRepairSummary> RepairAsync(
        string workspaceId,
        IReadOnlyList<TaskExecutionTrackingDecision> decisions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(decisions);

        var repairedByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!string.Equals(decision.WorkspaceId, workspaceId, StringComparison.Ordinal))
                continue;

            var repaired = decision.Code switch
            {
                "terminal_binding_still_active" or "blocked_binding_still_active" =>
                    await TryCleanupTerminalBindingAsync(decision, ct),
                "legacy_assignment_execution_missing" =>
                    await TryCleanupLegacyAssignmentAsync(decision, ct),
                "legacy_delivery_terminal_without_execution" =>
                    await TryCleanupLegacyAssignmentAsync(decision, ct),
                "continuation_lease_expired" => await TryRecoverContinuationLeaseAsync(decision, ct),
                "continuation_intent_missing" or "next_iteration_intent_missing" =>
                    await TryRestoreContinuationIntentAsync(decision, ct),
                _ => false,
            };
            if (!repaired)
                continue;
            repairedByCode[decision.Code] = repairedByCode.GetValueOrDefault(decision.Code) + 1;
        }

        return new TaskExecutionRepairSummary
        {
            Examined = decisions.Count,
            Repaired = repairedByCode.Values.Sum(),
            RepairedByCode = repairedByCode,
        };
    }

    private async Task<bool> TryCleanupTerminalBindingAsync(
        TaskExecutionTrackingDecision decision,
        CancellationToken ct)
    {
        if (decision.GoalRunId is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var binding = await db.TaskGoalBindings.SingleOrDefaultAsync(item =>
            item.WorkspaceId == decision.WorkspaceId
            && item.TaskId == decision.TaskId
            && item.GoalRunId == decision.GoalRunId
            && item.Status == "active", ct);
        if (binding is null)
            return await RollbackFalseAsync(tx, ct);
        var task = await db.WorkspaceTasks.SingleOrDefaultAsync(item =>
            item.WorkspaceId == binding.WorkspaceId && item.TaskId == binding.TaskId, ct);
        var goal = await db.GoalRuns.SingleOrDefaultAsync(item => item.GoalRunId == binding.GoalRunId, ct);
        if (task is null || goal is null
            || (!TaskStateMachine.IsTerminal(task.Status)
                && !GoalStateMachine.IsTerminal(goal.Status)
                && goal.Status != GoalPhase.Blocked))
        {
            return await RollbackFalseAsync(tx, ct);
        }

        var now = timeProvider.GetUtcNow();
        binding.Status = "terminal";
        binding.ReleasedAtUtc = now;
        if (binding.ReservationId is not null)
        {
            var reservation = await db.AgentExecutionReservations.SingleOrDefaultAsync(item =>
                item.ReservationId == binding.ReservationId
                && item.FencingToken == binding.ReservationFencingToken, ct);
            if (reservation is not null && reservation.Status == "active")
            {
                reservation.Status = "released";
                reservation.ReleaseReason = decision.Code == "blocked_binding_still_active"
                    ? "tracker_blocked_cleanup"
                    : "tracker_terminal_cleanup";
                reservation.ReleasedAtUtc = now;
                reservation.UpdatedAtUtc = now;
            }
        }

        var taskChanged = false;
        if (binding.AssignmentId is not null)
        {
            var assignment = await db.TaskAssignmentAttempts.SingleOrDefaultAsync(item =>
                item.AttemptId == binding.AssignmentId, ct);
            if (assignment is not null && assignment.ReleasedAtUtc is null)
            {
                assignment.Status = task.Status == WorkspaceTaskStatus.Completed
                    ? AssignmentAttemptStatus.Completed
                    : AssignmentAttemptStatus.Failed;
                assignment.ReleasedAtUtc = now;
                assignment.UpdatedAtUtc = now;
            }
            if (string.Equals(task.ActiveAssignmentId, binding.AssignmentId, StringComparison.Ordinal))
            {
                task.ActiveAssignmentId = null;
                taskChanged = true;
            }
        }
        if (taskChanged)
        {
            task.Version++;
            task.UpdatedAtUtc = now;
            await AppendTaskUpdatedEventAsync(db, task, binding, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogWarning(
            "[TaskExecutionRepair] cleaned terminal binding task={TaskId} goal={GoalRunId}",
            decision.TaskId,
            decision.GoalRunId);
        return true;
    }

    private async Task<bool> TryCleanupLegacyAssignmentAsync(
        TaskExecutionTrackingDecision decision,
        CancellationToken ct)
    {
        if (decision.AssignmentId is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var now = timeProvider.GetUtcNow();
        var task = await db.WorkspaceTasks.SingleOrDefaultAsync(item =>
            item.WorkspaceId == decision.WorkspaceId && item.TaskId == decision.TaskId, ct);
        var assignment = await db.TaskAssignmentAttempts.SingleOrDefaultAsync(item =>
            item.AttemptId == decision.AssignmentId
            && item.WorkspaceId == decision.WorkspaceId
            && item.TaskId == decision.TaskId, ct);
        if (task is null || assignment is null
            || task.ActiveAssignmentId != assignment.AttemptId
            || assignment.ReleasedAtUtc is not null
            || await db.TaskGoalBindings.AnyAsync(item =>
                item.WorkspaceId == decision.WorkspaceId
                && item.TaskId == decision.TaskId
                && item.Status == "active", ct))
        {
            return await RollbackFalseAsync(tx, ct);
        }

        var executionBinding = await db.TaskExecutionBindings
            .Where(item => item.TaskId == decision.TaskId
                && item.AssignmentId == decision.AssignmentId)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
        var delivery = executionBinding is null
            ? null
            : await db.MessageDeliveries.SingleOrDefaultAsync(item =>
                item.DeliveryId == executionBinding.DeliveryId, ct);
        var lastProgress = Latest(
            task.UpdatedAtUtc,
            assignment.UpdatedAtUtc,
            executionBinding?.BoundAtUtc,
            UnixMs(delivery?.UpdatedAt));
        if (executionBinding is null || delivery is null
            || !string.IsNullOrWhiteSpace(executionBinding.ExecutionId)
            || !string.IsNullOrWhiteSpace(executionBinding.SessionId)
            || !string.IsNullOrWhiteSpace(delivery.ClaimedByExecutionId)
            || !CanCleanupLegacyDelivery(delivery, lastProgress, now))
        {
            return await RollbackFalseAsync(tx, ct);
        }

        if (TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Blocked))
            task.Status = WorkspaceTaskStatus.Blocked;
        else if (task.Status is not (WorkspaceTaskStatus.Blocked or WorkspaceTaskStatus.NeedsReview))
            return await RollbackFalseAsync(tx, ct);
        var deliveryTerminal = decision.Code == "legacy_delivery_terminal_without_execution";
        task.BlockerKind = deliveryTerminal
            ? "delivery_terminal_without_execution"
            : "assignment_execution_missing";
        task.BlockerReason = deliveryTerminal
            ? $"Delivery ended as {delivery.Status}, but no canonical execution claimed the assignment."
            : "Delivery was acknowledged, but no canonical execution claimed the assignment before the stall threshold.";
        task.ActiveAssignmentId = null;
        task.Version++;
        task.UpdatedAtUtc = now;
        task.UpdatedBy = "task-execution-repair";
        assignment.Status = AssignmentAttemptStatus.Failed;
        assignment.ReleasedAtUtc = now;
        assignment.UpdatedAtUtc = now;
        await AppendLegacyTaskBlockedEventAsync(
            db,
            task,
            assignment,
            task.BlockerKind,
            now,
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogWarning(
            "[TaskExecutionRepair] released unclaimed legacy assignment task={TaskId} assignment={AssignmentId} agent={AgentId}",
            decision.TaskId,
            assignment.AttemptId,
            assignment.AgentId);
        return true;
    }

    private bool CanCleanupLegacyDelivery(
        MessageDeliveryEntity delivery,
        DateTimeOffset? lastProgress,
        DateTimeOffset now)
    {
        if (delivery.Status is "dead_letter" or "failed" or "cancelled")
            return true;
        return string.Equals(delivery.Status, "delivered", StringComparison.Ordinal)
            && lastProgress is not null
            && now - lastProgress.Value > _options.TrackerStallThreshold;
    }

    private async Task<bool> TryRecoverContinuationLeaseAsync(
        TaskExecutionTrackingDecision decision,
        CancellationToken ct)
    {
        if (decision.OutboxId is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = timeProvider.GetUtcNow();
        var outbox = await db.GoalOutbox.SingleOrDefaultAsync(item =>
            item.OutboxId == decision.OutboxId
            && item.GoalRunId == decision.GoalRunId, ct);
        if (outbox is null
            || outbox.Status != GoalOutboxValues.Leased
            || outbox.LeaseUntilUtc is null
            || outbox.LeaseUntilUtc > now)
        {
            return await RollbackFalseAsync(tx, ct);
        }

        outbox.Status = GoalOutboxValues.Pending;
        outbox.LeaseOwner = null;
        outbox.LeaseUntilUtc = null;
        outbox.DueAtUtc = now;
        outbox.LastError = "tracker_recovered_expired_lease";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        outboxSignal.Signal();
        logger.LogWarning(
            "[TaskExecutionRepair] recovered expired continuation lease outbox={OutboxId} goal={GoalRunId}",
            outbox.OutboxId,
            outbox.GoalRunId);
        return true;
    }

    private async Task<bool> TryRestoreContinuationIntentAsync(
        TaskExecutionTrackingDecision decision,
        CancellationToken ct)
    {
        if (decision.GoalRunId is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = timeProvider.GetUtcNow();
        var binding = await db.TaskGoalBindings.SingleOrDefaultAsync(item =>
            item.WorkspaceId == decision.WorkspaceId
            && item.TaskId == decision.TaskId
            && item.GoalRunId == decision.GoalRunId
            && item.Status == "active", ct);
        if (binding is null)
            return await RollbackFalseAsync(tx, ct);
        var goal = await db.GoalRuns.SingleOrDefaultAsync(item => item.GoalRunId == binding.GoalRunId, ct);
        var task = await db.WorkspaceTasks.SingleOrDefaultAsync(item =>
            item.WorkspaceId == binding.WorkspaceId && item.TaskId == binding.TaskId, ct);
        var reservation = binding.ReservationId is null
            ? null
            : await db.AgentExecutionReservations.SingleOrDefaultAsync(item =>
                item.ReservationId == binding.ReservationId
                && item.FencingToken == binding.ReservationFencingToken, ct);
        if (goal is null || task is null || reservation is null
            || goal.Status != GoalPhase.Active
            || goal.IterationsSettled != goal.IterationsStarted
            || goal.IterationsStarted >= goal.MaxIterations
            || task.Version != binding.ExpectedTaskVersion
            || task.ActiveAssignmentId != binding.AssignmentId
            || reservation.Status != "active"
            || reservation.LeaseUntilUtc <= now)
        {
            return await RollbackFalseAsync(tx, ct);
        }
        if (await db.GoalIterations.AnyAsync(item =>
                item.GoalRunId == goal.GoalRunId
                && (item.Status == "accepted" || item.Status == "running"), ct)
            || await db.GoalOutbox.AnyAsync(item =>
                item.GoalRunId == goal.GoalRunId
                && item.Kind == GoalOutboxValues.Continuation
                && (item.Status == GoalOutboxValues.Pending
                    || item.Status == GoalOutboxValues.Leased), ct))
        {
            return await RollbackFalseAsync(tx, ct);
        }

        var nextIteration = goal.IterationsStarted + 1;
        var outboxId = $"gc-{goal.GoalRunId}-{goal.ActivationEpoch}-{nextIteration}";
        if (await db.GoalOutbox.AnyAsync(item => item.OutboxId == outboxId, ct))
            return await RollbackFalseAsync(tx, ct);
        db.GoalOutbox.Add(new GoalOutboxEntity
        {
            OutboxId = outboxId,
            GoalRunId = goal.GoalRunId,
            ActivationEpoch = goal.ActivationEpoch,
            AggregateVersion = goal.AggregateVersion,
            Kind = GoalOutboxValues.Continuation,
            IdempotencyKey = outboxId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                goalRunId = goal.GoalRunId,
                objectiveVersion = goal.ObjectiveVersion,
                iterationNo = nextIteration,
                taskId = binding.TaskId,
                expectedTaskVersion = binding.ExpectedTaskVersion,
                reservationFencingToken = binding.ReservationFencingToken,
            }, JsonOpts),
            Status = GoalOutboxValues.Pending,
            DueAtUtc = now,
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        outboxSignal.Signal();
        logger.LogWarning(
            "[TaskExecutionRepair] restored missing continuation task={TaskId} goal={GoalRunId} iteration={Iteration}",
            decision.TaskId,
            goal.GoalRunId,
            nextIteration);
        return true;
    }

    private static async Task AppendTaskUpdatedEventAsync(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        TaskGoalBindingEntity binding,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var next = (await db.TaskEvents
            .Where(item => item.TaskId == task.TaskId)
            .MaxAsync(item => (long?)item.Sequence, ct) ?? 0) + 1;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = $"tracker-cleanup-{task.TaskId}-{task.Version}",
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = next,
            EventType = TaskEventType.TaskUpdated,
            AssignmentId = binding.AssignmentId,
            AgentId = binding.AgentInstanceId,
            SessionId = binding.GoalRunId,
            CorrelationId = binding.GoalRunId,
            CausationId = binding.BindingId,
            CreatedAtUtc = now,
        });
    }

    private static async Task AppendLegacyTaskBlockedEventAsync(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        TaskAssignmentAttemptEntity assignment,
        string decisionCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var next = (await db.TaskEvents
            .Where(item => item.TaskId == task.TaskId)
            .MaxAsync(item => (long?)item.Sequence, ct) ?? 0) + 1;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = $"tracker-legacy-blocked-{task.TaskId}-{task.Version}",
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = next,
            EventType = TaskEventType.TaskBlocked,
            AssignmentId = assignment.AttemptId,
            AgentId = assignment.AgentId,
            DecisionCode = decisionCode,
            CorrelationId = task.TaskId,
            CausationId = assignment.AttemptId,
            CreatedAtUtc = now,
        });
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values)
        => values.Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty().Max();

    private static DateTimeOffset? UnixMs(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    private static async Task<bool> RollbackFalseAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        return false;
    }
}
