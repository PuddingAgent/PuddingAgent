using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services.Tasks;

/// <summary>Options for the completion-facts settlement service (task 4ed930e7).</summary>
public sealed class CompletionSettlementOptions
{
    public const string SectionName = "CompletionSettlement";

    /// <summary>
    /// Settlement grace after an ExecutionRun turns terminal: within the grace the
    /// explicit task/goal settlement is still awaited; only after the grace does the
    /// deterministic completion-facts settlement take over.
    /// </summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Completion-facts settlement result. When Settled=false, Code carries a stable no-op reason.</summary>
public sealed record CompletionSettlementResult(
    bool Settled,
    string? Code,
    string? Action,
    int? TaskVersion);

/// <summary>
/// Task 4ed930e7 "P0 unified completion facts" - completion-facts settlement service.
/// <para>
/// Canonical rule: a terminal ExecutionRun is NOT a Task Completed. When a Task still
/// holds an active assignment whose binding/delivery claim points to a canonical
/// ExecutionRun that is terminal (succeeded/failed/cancelled/lease_lost) and the
/// settlement grace has elapsed, this service re-reads Task/Assignment/Binding/Run
/// inside a Serializable transaction and commits in one unit:
/// 1) TaskStateMachine Block/Fail (succeeded without disposition settlement -> Blocked
///    with blockerKind=execution_terminal_without_task_settlement; failed/cancelled/
///    lease_lost -> Failed/Blocked with a stable code);
/// 2) binding backfill of execution_id/session_id;
/// 3) release of the attempt, active TaskGoalBinding and reservations;
/// 4) a causal TaskEvent (CausationId=assignment, CorrelationId=run).
/// After commit the Agent availability projection is rebuilt (best-effort).
/// </para>
/// <para>
/// Idempotency: after a committed settlement ActiveAssignmentId is null, so repeated
/// calls no-op; the event id is deterministic
/// (completion-settlement-{taskId}-{version}). Transaction conflicts or drifted facts
/// roll back to a no-op and never re-apply a stale decision (design doc section 4.3).
/// </para>
/// </summary>
public sealed class TaskCompletionSettlementService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IAgentAvailabilityProjectionStore availabilityStore,
    IOptions<CompletionSettlementOptions> options,
    TimeProvider timeProvider,
    ILogger<TaskCompletionSettlementService> logger)
{
    /// <summary>Terminal ExecutionRun statuses (design doc 3.1 pure function; unknown values fail closed).</summary>
    internal static bool IsExecutionRunTerminal(string? status) =>
        status is "succeeded" or "failed" or "cancelled" or "lease_lost";

    /// <summary>Settle the completion facts of one task. Idempotent and re-entrant; no-op without matching facts.</summary>
    public async Task<CompletionSettlementResult> SettleAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var now = timeProvider.GetUtcNow();

        // 4.3-2: re-read the Task inside the Serializable transaction (fencing baseline).
        var task = await db.WorkspaceTasks.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.TaskId == taskId, ct);
        if (task is null)
            return await NoopAsync(tx, "task_not_found", ct);
        if (task.ActiveAssignmentId is null)
            return await NoopAsync(tx, "no_active_assignment", ct);

        var assignmentId = task.ActiveAssignmentId;

        // 4.3-3: re-read the Assignment; it must still be unreleased.
        var attempt = await db.TaskAssignmentAttempts.SingleOrDefaultAsync(
            item => item.AttemptId == assignmentId
                && item.WorkspaceId == workspaceId
                && item.TaskId == taskId, ct);
        if (attempt is null)
            return await NoopAsync(tx, "assignment_missing", ct);
        if (attempt.ReleasedAtUtc is not null)
            return await NoopAsync(tx, "assignment_already_released", ct);

        // 4.3-4: re-read Binding/Run; claim priority = binding.ExecutionId -> delivery.ClaimedByExecutionId.
        var binding = await db.TaskExecutionBindings
            .Where(item => item.TaskId == taskId && item.AssignmentId == assignmentId)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
        var claim = binding?.ExecutionId;
        if (string.IsNullOrWhiteSpace(claim) && binding is not null)
        {
            var delivery = await db.MessageDeliveries.AsNoTracking().SingleOrDefaultAsync(
                item => item.DeliveryId == binding.DeliveryId, ct);
            claim = delivery?.ClaimedByExecutionId;
        }

        if (string.IsNullOrWhiteSpace(claim))
            return await NoopAsync(tx, "execution_claim_missing", ct);

        // Exact-index lookups only: run_id first; a historical command id is accepted
        // only when it matches exactly one row (no fuzzy matching).
        var run = await db.ExecutionRuns.AsNoTracking().SingleOrDefaultAsync(
            item => item.RunId == claim, ct)
            ?? await db.ExecutionRuns.AsNoTracking().SingleOrDefaultAsync(
                item => item.CommandId == claim, ct);
        if (run is null)
            return await NoopAsync(tx, "execution_run_missing", ct);
        if (!IsExecutionRunTerminal(run.Status))
            return await NoopAsync(tx, "execution_run_not_terminal", ct);
        if (run.CompletedAt is null)
            return await NoopAsync(tx, "run_terminal_time_missing", ct);

        var runTerminalAt = DateTimeOffset.FromUnixTimeMilliseconds(run.CompletedAt.Value);
        if (now - runTerminalAt < options.Value.Grace)
            return await NoopAsync(tx, "within_settlement_grace", ct);

        var hasCompletedEvent = await db.TaskEvents.AnyAsync(
            item => item.TaskId == taskId && item.EventType == TaskEventType.TaskCompleted, ct);

        string action;
        string decisionCode;
        TaskEventType eventType;
        var attemptStatus = AssignmentAttemptStatus.Failed;

        if (task.Status == WorkspaceTaskStatus.Completed)
        {
            // Legacy PATCH channel marked the task Completed but dropped the facts
            // (no TaskCompleted event, empty binding, unreleased attempt): backfill
            // facts only - the terminal status itself is never flipped.
            if (string.Equals(run.Status, "succeeded", StringComparison.Ordinal) && !hasCompletedEvent)
            {
                eventType = TaskEventType.TaskCompleted;
                decisionCode = "settlement_backfill_completed";
                action = "completed_backfill";
                attemptStatus = AssignmentAttemptStatus.Completed;
            }
            else
            {
                eventType = TaskEventType.TaskUpdated;
                decisionCode = string.Equals(run.Status, "succeeded", StringComparison.Ordinal)
                    ? "settlement_facts_idempotent"
                    : "execution_terminal_mismatch";
                action = "facts_reconciled";
            }
        }
        else if (string.Equals(run.Status, "succeeded", StringComparison.Ordinal))
        {
            // 4.3-5: succeeded without Task settlement -> Blocked; never auto-complete.
            if (!TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Blocked))
                return await NoopAsync(tx, "inconsistent_state_no_transition", ct);
            task.Status = WorkspaceTaskStatus.Blocked;
            task.BlockerKind = "execution_terminal_without_task_settlement";
            task.BlockerReason =
                $"Execution run '{run.RunId}' succeeded, but the task was never settled with an evidence-bearing disposition.";
            eventType = TaskEventType.TaskBlocked;
            decisionCode = task.BlockerKind;
            action = "blocked";
        }
        else
        {
            // 4.3-6: failed/cancelled/lease_lost -> Failed/Blocked per the state machine.
            var failureCode = $"execution_run_{run.Status}";
            if (TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Failed))
            {
                task.Status = WorkspaceTaskStatus.Failed;
                task.FailedAtUtc = now;
                task.FailureCode = failureCode;
                task.FailureReason =
                    $"Execution run '{run.RunId}' ended as '{run.Status}' before the task settled.";
                eventType = TaskEventType.TaskFailed;
                action = "failed";
            }
            else if (TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Blocked))
            {
                task.Status = WorkspaceTaskStatus.Blocked;
                task.BlockerKind = failureCode;
                task.BlockerReason =
                    $"Execution run '{run.RunId}' ended as '{run.Status}' before the task settled.";
                eventType = TaskEventType.TaskBlocked;
                action = "blocked";
            }
            else
            {
                return await NoopAsync(tx, "inconsistent_state_no_transition", ct);
            }

            decisionCode = failureCode;
        }

        // One commit unit: ownership release + facts backfill + causal event.
        if (string.Equals(task.ActiveAssignmentId, assignmentId, StringComparison.Ordinal))
            task.ActiveAssignmentId = null;
        task.Version += 1;
        task.UpdatedAtUtc = now;
        task.UpdatedBy = "completion-settlement";

        attempt.Status = attemptStatus;
        attempt.ReleasedAtUtc = now;
        attempt.UpdatedAtUtc = now;

        if (binding is not null)
        {
            // Idempotent backfill: only fill when still empty.
            binding.ExecutionId ??= run.RunId;
            binding.SessionId ??= run.ConversationId;
        }

        var goalBindings = await db.TaskGoalBindings
            .Where(item => item.WorkspaceId == workspaceId
                && item.TaskId == taskId
                && item.AssignmentId == assignmentId
                && item.Status == "active")
            .ToListAsync(ct);
        foreach (var goalBinding in goalBindings)
        {
            goalBinding.Status = "terminal";
            goalBinding.ReleasedAtUtc = now;
        }

        var reservations = await db.AgentExecutionReservations
            .Where(item => item.WorkspaceId == workspaceId
                && item.TaskId == taskId
                && item.Status == "active")
            .ToListAsync(ct);
        foreach (var reservation in reservations)
        {
            reservation.Status = "released";
            reservation.ReleaseReason = "completion_settlement";
            reservation.ReleasedAtUtc = now;
            reservation.UpdatedAtUtc = now;
        }

        var sequence = (await db.TaskEvents
            .Where(item => item.TaskId == taskId)
            .MaxAsync(item => (long?)item.Sequence, ct) ?? 0) + 1;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = $"completion-settlement-{taskId}-{task.Version}",
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = sequence,
            EventType = eventType,
            AssignmentId = assignmentId,
            AgentId = attempt.AgentId,
            ExecutionId = run.RunId,
            SessionId = run.ConversationId,
            DecisionCode = decisionCode,
            CorrelationId = run.RunId,
            CausationId = assignmentId,
            CreatedAtUtc = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or DBConcurrencyException)
        {
            // 4.3-9: transaction conflict fails closed as a no-op; never re-apply stale decisions.
            logger.LogWarning(ex,
                "[CompletionSettlement] transaction conflict, settling aborted task={TaskId}",
                taskId);
            return new CompletionSettlementResult(false, "settlement_conflict", null, null);
        }

        // Rebuild availability after commit: terminal facts are durable; a rebuild
        // failure must not roll back committed facts.
        try
        {
            await availabilityStore.RebuildAsync(workspaceId, attempt.AgentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[CompletionSettlement] availability rebuild failed task={TaskId} agent={AgentId}",
                taskId,
                attempt.AgentId);
        }

        logger.LogWarning(
            "[CompletionSettlement] settled task={TaskId} assignment={AssignmentId} run={RunId} action={Action}",
            taskId,
            assignmentId,
            run.RunId,
            action);
        return new CompletionSettlementResult(true, null, action, task.Version);
    }

    private static async Task<CompletionSettlementResult> NoopAsync(
        IDbContextTransaction tx,
        string code,
        CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        return new CompletionSettlementResult(false, code, null, null);
    }
}
