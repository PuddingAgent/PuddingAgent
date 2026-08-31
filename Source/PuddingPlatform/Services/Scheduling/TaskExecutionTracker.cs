using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Correlates Task, Assignment, Reservation, Goal, Iteration, command/run and
/// outbox facts. This evaluator is deliberately read-only: a later repair
/// coordinator must re-read every fence before it may mutate authoritative state.
/// </summary>
public sealed class TaskExecutionTracker(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IOptions<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider) : ITaskExecutionTracker
{
    private readonly TaskAutoDispatchOptions _options = options.Value;

    public async Task<IReadOnlyList<TaskExecutionTrackingDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bindings = await db.TaskGoalBindings.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Status == "active")
            .OrderBy(item => item.BindingId)
            .Take(limit)
            .ToListAsync(ct);
        var taskIds = bindings.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).ToArray();
        var assignmentIds = bindings.Where(item => item.AssignmentId != null)
            .Select(item => item.AssignmentId!).Distinct(StringComparer.Ordinal).ToArray();
        var goalIds = bindings.Select(item => item.GoalRunId).Distinct(StringComparer.Ordinal).ToArray();
        var reservationIds = bindings.Where(item => item.ReservationId != null)
            .Select(item => item.ReservationId!).Distinct(StringComparer.Ordinal).ToArray();
        var taskPlanIds = bindings.Where(item => item.TaskPlanId != null)
            .Select(item => item.TaskPlanId!).Distinct(StringComparer.Ordinal).ToArray();

        var tasks = await db.WorkspaceTasks.AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskId))
            .ToDictionaryAsync(item => item.TaskId, StringComparer.Ordinal, ct);
        var assignments = assignmentIds.Length == 0
            ? new Dictionary<string, TaskAssignmentAttemptEntity>(StringComparer.Ordinal)
            : await db.TaskAssignmentAttempts.AsNoTracking()
                .Where(item => assignmentIds.Contains(item.AttemptId))
                .ToDictionaryAsync(item => item.AttemptId, StringComparer.Ordinal, ct);
        var goals = await db.GoalRuns.AsNoTracking()
            .Where(item => goalIds.Contains(item.GoalRunId))
            .ToDictionaryAsync(item => item.GoalRunId, StringComparer.Ordinal, ct);
        var reservations = reservationIds.Length == 0
            ? new Dictionary<string, AgentExecutionReservationEntity>(StringComparer.Ordinal)
            : await db.AgentExecutionReservations.AsNoTracking()
                .Where(item => reservationIds.Contains(item.ReservationId))
                .ToDictionaryAsync(item => item.ReservationId, StringComparer.Ordinal, ct);
        var plans = taskPlanIds.Length == 0
            ? new Dictionary<string, TaskPlanRunEntity>(StringComparer.Ordinal)
            : await db.TaskPlanRuns.AsNoTracking()
                .Where(item => taskPlanIds.Contains(item.PlanId))
                .ToDictionaryAsync(item => item.PlanId, StringComparer.Ordinal, ct);
        var planNodes = taskPlanIds.Length == 0
            ? []
            : await db.TaskNodes.AsNoTracking()
                .Where(item => taskPlanIds.Contains(item.PlanId) && item.Depth == 1)
                .OrderBy(item => item.SequenceNo)
                .ToListAsync(ct);
        var currentWorkUnits = planNodes
            .GroupBy(item => item.PlanId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(item => item.Status is not ("Completed" or "Cancelled" or "Superseded"))
                    ?? group.Last(),
                StringComparer.Ordinal);
        var iterations = await db.GoalIterations.AsNoTracking()
            .Where(item => goalIds.Contains(item.GoalRunId))
            .ToListAsync(ct);
        var outboxes = await db.GoalOutbox.AsNoTracking()
            .Where(item => goalIds.Contains(item.GoalRunId)
                && item.Kind == GoalOutboxValues.Continuation)
            .ToListAsync(ct);
        var commandIds = iterations.Where(item => item.CommandId != null)
            .Select(item => item.CommandId!).Distinct(StringComparer.Ordinal).ToArray();
        var commands = commandIds.Length == 0
            ? new Dictionary<string, ChatExecutionCommandEntity>(StringComparer.Ordinal)
            : await db.ChatExecutionCommands.AsNoTracking()
                .Where(item => commandIds.Contains(item.CommandId))
                .ToDictionaryAsync(item => item.CommandId, StringComparer.Ordinal, ct);
        var runs = commandIds.Length == 0
            ? []
            : await db.ExecutionRuns.AsNoTracking()
                .Where(item => commandIds.Contains(item.CommandId))
                .ToListAsync(ct);

        var latestIterations = iterations
            .GroupBy(item => item.GoalRunId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.ActivationEpoch)
                    .ThenByDescending(item => item.IterationNo)
                    .ThenByDescending(item => item.CreatedAtUtc)
                    .First(),
                StringComparer.Ordinal);
        var latestOutboxes = outboxes
            .GroupBy(item => item.GoalRunId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.ActivationEpoch)
                    .ThenByDescending(item => item.CreatedAtUtc)
                    .First(),
                StringComparer.Ordinal);
        var latestRuns = runs
            .GroupBy(item => item.CommandId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Attempt)
                    .ThenByDescending(item => item.FencingToken)
                    .First(),
                StringComparer.Ordinal);

        var results = bindings.Select(binding => EvaluateOne(
                binding,
                tasks.GetValueOrDefault(binding.TaskId),
                binding.AssignmentId is null ? null : assignments.GetValueOrDefault(binding.AssignmentId),
                goals.GetValueOrDefault(binding.GoalRunId),
                binding.ReservationId is null ? null : reservations.GetValueOrDefault(binding.ReservationId),
                binding.TaskPlanId is null ? null : plans.GetValueOrDefault(binding.TaskPlanId),
                binding.TaskPlanId is null ? null : currentWorkUnits.GetValueOrDefault(binding.TaskPlanId),
                latestIterations.GetValueOrDefault(binding.GoalRunId),
                latestOutboxes.GetValueOrDefault(binding.GoalRunId),
                commands,
                latestRuns,
                now))
            .ToList();
        if (results.Count < limit)
        {
            var boundAssignmentIds = bindings
                .Where(item => item.AssignmentId is not null)
                .Select(item => item.AssignmentId!)
                .ToArray();
            results.AddRange(await EvaluateLegacyAssignmentsAsync(
                db,
                workspaceId,
                boundAssignmentIds,
                limit - results.Count,
                now,
                ct));
        }
        return results;
    }

    private async Task<IReadOnlyList<TaskExecutionTrackingDecision>> EvaluateLegacyAssignmentsAsync(
        PlatformDbContext db,
        string workspaceId,
        IReadOnlyCollection<string> boundAssignmentIds,
        int limit,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (limit <= 0)
            return [];

        var candidates = await (
                from attempt in db.TaskAssignmentAttempts.AsNoTracking()
                join task in db.WorkspaceTasks.AsNoTracking()
                    on new { attempt.WorkspaceId, attempt.TaskId }
                    equals new { task.WorkspaceId, task.TaskId }
                where attempt.WorkspaceId == workspaceId
                    && attempt.ReleasedAtUtc == null
                    && task.ActiveAssignmentId == attempt.AttemptId
                    && !boundAssignmentIds.Contains(attempt.AttemptId)
                    && task.Status != WorkspaceTaskStatus.Completed
                    && task.Status != WorkspaceTaskStatus.Failed
                    && task.Status != WorkspaceTaskStatus.Cancelled
                    && task.Status != WorkspaceTaskStatus.Archived
                // SQLite cannot translate DateTimeOffset ORDER BY. AttemptId is a
                // stable bounded scan key; recency is evaluated in memory below.
                orderby attempt.AttemptId
                select new { Attempt = attempt, Task = task })
            .Take(limit)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return [];

        var assignmentIds = candidates.Select(item => item.Attempt.AttemptId).ToArray();
        var executionBindings = await db.TaskExecutionBindings.AsNoTracking()
            .Where(item => assignmentIds.Contains(item.AssignmentId))
            .ToListAsync(ct);
        var deliveryIds = executionBindings.Select(item => item.DeliveryId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var deliveries = deliveryIds.Length == 0
            ? new Dictionary<string, MessageDeliveryEntity>(StringComparer.Ordinal)
            : await db.MessageDeliveries.AsNoTracking()
                .Where(item => deliveryIds.Contains(item.DeliveryId))
                .ToDictionaryAsync(item => item.DeliveryId, StringComparer.Ordinal, ct);

        return candidates.Select(candidate =>
        {
            var executionBinding = executionBindings
                .OrderByDescending(item => item.Id)
                .FirstOrDefault(item => item.AssignmentId == candidate.Attempt.AttemptId);
            var delivery = executionBinding is null
                ? null
                : deliveries.GetValueOrDefault(executionBinding.DeliveryId);
            var lastProgress = Latest(
                candidate.Task.UpdatedAtUtc,
                candidate.Attempt.UpdatedAtUtc,
                executionBinding?.BoundAtUtc,
                UnixMs(delivery?.UpdatedAt));

            TaskExecutionTrackingDecision Result(
                TaskExecutionTrackingVerdict verdict,
                string code) => new()
            {
                WorkspaceId = workspaceId,
                TaskId = candidate.Task.TaskId,
                AgentId = candidate.Attempt.AgentId,
                AssignmentId = candidate.Attempt.AttemptId,
                TaskStatus = candidate.Task.Status,
                Verdict = verdict,
                Code = code,
                ObservedAtUtc = now,
                LastProgressAtUtc = lastProgress,
            };

            if (executionBinding is null)
                return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_binding_missing");
            if (delivery is null)
                return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_delivery_missing");
            if (!string.IsNullOrWhiteSpace(executionBinding.ExecutionId)
                || !string.IsNullOrWhiteSpace(executionBinding.SessionId)
                || !string.IsNullOrWhiteSpace(delivery.ClaimedByExecutionId))
            {
                return Result(TaskExecutionTrackingVerdict.Healthy, "legacy_execution_claimed");
            }
            if (delivery.Status is "dead_letter" or "failed" or "cancelled")
            {
                return Result(
                    TaskExecutionTrackingVerdict.CleanupRequired,
                    "legacy_delivery_terminal_without_execution");
            }
            if (string.Equals(delivery.Status, "delivered", StringComparison.Ordinal)
                && IsOverdue(lastProgress, now))
            {
                return Result(TaskExecutionTrackingVerdict.CleanupRequired, "legacy_assignment_execution_missing");
            }
            return Result(TaskExecutionTrackingVerdict.Waiting, "legacy_assignment_waiting_execution");
        }).ToArray();
    }

    private TaskExecutionTrackingDecision EvaluateOne(
        TaskGoalBindingEntity binding,
        WorkspaceTaskEntity? task,
        TaskAssignmentAttemptEntity? assignment,
        GoalRunEntity? goal,
        AgentExecutionReservationEntity? reservation,
        TaskPlanRunEntity? plan,
        TaskNodeEntity? workUnit,
        GoalIterationEntity? iteration,
        GoalOutboxEntity? outbox,
        IReadOnlyDictionary<string, ChatExecutionCommandEntity> commands,
        IReadOnlyDictionary<string, ExecutionRunEntity> runs,
        DateTimeOffset now)
    {
        var command = iteration?.CommandId is null
            ? null
            : commands.GetValueOrDefault(iteration.CommandId);
        var run = iteration?.CommandId is null
            ? null
            : runs.GetValueOrDefault(iteration.CommandId);
        var lastProgress = Latest(
            binding.CreatedAtUtc,
            task?.UpdatedAtUtc,
            assignment?.UpdatedAtUtc,
            goal?.UpdatedAtUtc,
            reservation?.UpdatedAtUtc,
            plan is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(plan.UpdatedAt),
            workUnit is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(workUnit.UpdatedAt),
            iteration?.SettledAtUtc ?? iteration?.StartedAtUtc ?? iteration?.CreatedAtUtc,
            outbox?.CompletedAtUtc ?? outbox?.CreatedAtUtc,
            UnixMs(command?.CompletedAt ?? command?.StartedAt ?? command?.CreatedAt),
            UnixMs(run?.CompletedAt ?? run?.StartedAt));

        TaskExecutionTrackingDecision Result(TaskExecutionTrackingVerdict verdict, string code) => new()
        {
            WorkspaceId = binding.WorkspaceId,
            TaskId = binding.TaskId,
            AgentId = binding.AgentInstanceId,
            AssignmentId = binding.AssignmentId,
            GoalRunId = binding.GoalRunId,
            ReservationId = binding.ReservationId,
            TaskPlanId = binding.TaskPlanId,
            ExecutionPlanFingerprint = binding.PlanFingerprint,
            ExecutionPlanStatus = plan?.Status,
            WorkUnitKind = workUnit?.WorkUnitKind,
            WorkUnitStatus = workUnit?.Status,
            TaskStatus = task?.Status,
            GoalPhase = goal?.Status,
            IterationStatus = iteration?.Status,
            CommandStatus = command?.Status,
            RunStatus = run?.Status,
            OutboxStatus = outbox?.Status,
            OutboxId = outbox?.OutboxId,
            Verdict = verdict,
            Code = code,
            ObservedAtUtc = now,
            LastProgressAtUtc = lastProgress,
        };

        if (task is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "task_missing");
        if (assignment is null || binding.AssignmentId is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "assignment_missing");
        if (!string.Equals(task.ActiveAssignmentId, assignment.AttemptId, StringComparison.Ordinal)
            || assignment.ReleasedAtUtc is not null
            || !string.Equals(assignment.AgentId, binding.AgentInstanceId, StringComparison.Ordinal))
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "assignment_fence_mismatch");
        if (goal is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "goal_missing");
        if (binding.TaskPlanId is not null && plan is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "execution_plan_missing");
        if (plan is not null
            && (!string.Equals(plan.WorkspaceTaskId, binding.TaskId, StringComparison.Ordinal)
                || !string.Equals(plan.PlanFingerprint, binding.PlanFingerprint, StringComparison.Ordinal)))
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "execution_plan_fence_mismatch");
        if (plan is not null && workUnit is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "work_unit_missing");
        if (!string.Equals(goal.AgentInstanceId, binding.AgentInstanceId, StringComparison.Ordinal))
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "goal_agent_mismatch");
        if (TaskStateMachine.IsTerminal(task.Status) || GoalStateMachine.IsTerminal(goal.Status))
            return Result(TaskExecutionTrackingVerdict.CleanupRequired, "terminal_binding_still_active");
        // Blocked Task-bound Goals no longer own execution. Evaluate this before
        // reservation health because older settlement code already released the
        // reservation while leaving the binding and assignment active.
        if (goal.Status == GoalPhase.Blocked)
            return Result(TaskExecutionTrackingVerdict.CleanupRequired, "blocked_binding_still_active");
        if (reservation is null || binding.ReservationId is null)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "reservation_missing");
        if (!string.Equals(reservation.AgentId, binding.AgentInstanceId, StringComparison.Ordinal)
            || !string.Equals(reservation.TaskId, binding.TaskId, StringComparison.Ordinal)
            || !string.Equals(reservation.GoalRunId, binding.GoalRunId, StringComparison.Ordinal)
            || reservation.FencingToken != binding.ReservationFencingToken)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "reservation_fence_mismatch");
        if (!string.Equals(reservation.Status, "active", StringComparison.Ordinal)
            || reservation.ReleasedAtUtc is not null)
            return Result(TaskExecutionTrackingVerdict.Stalled, "reservation_not_active");
        if (reservation.LeaseUntilUtc <= now)
            return Result(TaskExecutionTrackingVerdict.Stalled, "reservation_expired");
        if (goal.Status == GoalPhase.Paused)
            return Result(TaskExecutionTrackingVerdict.Waiting, "goal_paused");
        if (goal.Status != GoalPhase.Active)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "goal_phase_unsupported");

        if (iteration is null)
        {
            if (outbox is null)
                return Result(TaskExecutionTrackingVerdict.Stalled, "continuation_intent_missing");
            return outbox.Status switch
            {
                GoalOutboxValues.Pending => Result(
                    IsOverdue(lastProgress, now)
                        ? TaskExecutionTrackingVerdict.Stalled
                        : TaskExecutionTrackingVerdict.Waiting,
                    IsOverdue(lastProgress, now) ? "continuation_pending_overdue" : "continuation_pending"),
                GoalOutboxValues.Leased when outbox.LeaseUntilUtc <= now =>
                    Result(TaskExecutionTrackingVerdict.Stalled, "continuation_lease_expired"),
                GoalOutboxValues.Leased => Result(TaskExecutionTrackingVerdict.Waiting, "continuation_leased"),
                GoalOutboxValues.DeadLettered => Result(TaskExecutionTrackingVerdict.Stalled, "continuation_dead_lettered"),
                GoalOutboxValues.Cancelled => Result(TaskExecutionTrackingVerdict.Stalled, "continuation_cancelled"),
                GoalOutboxValues.Completed => Result(TaskExecutionTrackingVerdict.Inconsistent, "accepted_iteration_missing"),
                _ => Result(TaskExecutionTrackingVerdict.Inconsistent, "continuation_status_unknown"),
            };
        }

        if (iteration.Status is "accepted" or "running")
        {
            if (command is null)
                return Result(TaskExecutionTrackingVerdict.Inconsistent, "execution_command_missing");
            if (command.Status is "failed" or "cancelled")
                return Result(TaskExecutionTrackingVerdict.Stalled, "command_terminal_iteration_open");
            if (run?.Status is "failed" or "cancelled")
                return Result(TaskExecutionTrackingVerdict.Stalled, "run_terminal_iteration_open");
            if (IsOverdue(lastProgress, now))
                return Result(TaskExecutionTrackingVerdict.Stalled, "iteration_terminal_fact_overdue");
            return Result(
                command.Status == "pending"
                    ? TaskExecutionTrackingVerdict.Waiting
                    : TaskExecutionTrackingVerdict.Healthy,
                command.Status == "pending" ? "execution_pending" : "iteration_running");
        }

        if (iteration.Status == "settled")
        {
            if (goal.IterationsSettled < goal.IterationsStarted)
                return Result(
                    IsOverdue(lastProgress, now)
                        ? TaskExecutionTrackingVerdict.Stalled
                        : TaskExecutionTrackingVerdict.Waiting,
                    IsOverdue(lastProgress, now) ? "settlement_projection_overdue" : "settlement_projection_pending");
            if (outbox is not null && outbox.Status is GoalOutboxValues.Pending or GoalOutboxValues.Leased)
                return Result(TaskExecutionTrackingVerdict.Waiting, "next_iteration_pending");
            return Result(TaskExecutionTrackingVerdict.Stalled, "next_iteration_intent_missing");
        }

        if (iteration.Status is "failed" or "cancelled")
            return Result(TaskExecutionTrackingVerdict.Stalled, "iteration_terminal_goal_active");
        return Result(TaskExecutionTrackingVerdict.Inconsistent, "iteration_status_unknown");
    }

    private bool IsOverdue(DateTimeOffset? lastProgress, DateTimeOffset now)
        => lastProgress is not null && now - lastProgress.Value > _options.TrackerStallThreshold;

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values)
        => values.Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty().Max();

    private static DateTimeOffset? UnixMs(long? completedAt = null)
        => completedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt.Value) : null;
}
