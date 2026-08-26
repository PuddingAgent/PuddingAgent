using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services.Goals;

public sealed record GoalSettlementCandidate
{
    public required string GoalIterationId { get; init; }
    public required string GoalRunId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public required int IterationNo { get; init; }
    public required int MaxIterations { get; init; }
    public required int IterationsStarted { get; init; }
    public required string Objective { get; init; }
    public required int ObjectiveVersion { get; init; }
    public required string TurnId { get; init; }
    public required string TerminalKind { get; init; }
    public required long TerminalSequence { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    public string? TaskId { get; init; }
    public int? TaskVersion { get; init; }
    public string? TaskStatus { get; init; }
    public string? TaskAcceptanceCriteria { get; init; }
    public bool HasPendingExecutionFacts { get; init; }
    public bool EvidenceComplete { get; init; }

    public GoalEvidenceCapsule ToCapsule() => new()
    {
        GoalRunId = GoalRunId,
        ActivationEpoch = ActivationEpoch,
        AggregateVersion = AggregateVersion,
        IterationNo = IterationNo,
        Objective = Objective,
        ObjectiveVersion = ObjectiveVersion,
        RemainingIterations = Math.Max(0, MaxIterations - IterationsStarted),
        TurnId = TurnId,
        TerminalKind = TerminalKind,
        TerminalSequence = TerminalSequence,
        EvidenceRefs = EvidenceRefs,
        TaskId = TaskId,
        TaskStatus = TaskStatus,
        TaskAcceptanceCriteria = TaskAcceptanceCriteria,
        HasPendingExecutionFacts = HasPendingExecutionFacts,
        EvidenceComplete = EvidenceComplete,
    };
}

/// <summary>
/// Canonical Turn 终态到 Goal Iteration/Verification/下一 continuation 的唯一事务协调器。
/// Verifier 只返回建议；本 Store 在事务内重验 epoch/version/Task 状态并独占终态写入。
/// </summary>
public sealed class GoalSettlementStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ICommittedEventSignal committedSignal,
    GoalOutboxSignal outboxSignal,
    IOptions<TaskBoundGoalOptions>? taskBoundOptions = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _reservationLease =
        taskBoundOptions?.Value.ReservationLease ?? TimeSpan.FromHours(2);

    public async Task<IReadOnlyList<GoalSettlementCandidate>> GetCandidatesAsync(
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var openCandidates = await db.GoalIterations.AsNoTracking()
            .Where(item => item.Status == "accepted" || item.Status == "running")
            .ToListAsync(ct);
        // SQLite 不支持 DateTimeOffset ORDER BY；活动 Iteration 受单 Goal 单飞约束有界。
        var openIterations = openCandidates
            .OrderBy(item => item.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 64))
            .ToList();
        var results = new List<GoalSettlementCandidate>();

        foreach (var iteration in openIterations)
        {
            if (string.IsNullOrWhiteSpace(iteration.TurnId))
                continue;
            var turn = await db.ConversationTurns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TurnId == iteration.TurnId, ct);
            if (turn?.TerminalSequence is null
                || turn.Status is not ("completed" or "failed" or "cancelled"))
                continue;
            var goal = await db.GoalRuns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GoalRunId == iteration.GoalRunId, ct);
            if (goal is null)
                continue;

            var binding = await db.TaskGoalBindings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GoalRunId == goal.GoalRunId, ct);
            WorkspaceTaskEntity? task = null;
            if (binding is not null)
            {
                task = await db.WorkspaceTasks.AsNoTracking().SingleOrDefaultAsync(
                    item => item.WorkspaceId == binding.WorkspaceId
                        && item.TaskId == binding.TaskId,
                    ct);
            }

            var evidenceEvents = await db.ConversationEvents.AsNoTracking()
                .Where(item => item.ConversationId == goal.CurrentConversationId
                    && item.TurnId == iteration.TurnId
                    && item.Sequence >= (iteration.AcceptedSequence ?? 0)
                    && item.Sequence <= turn.TerminalSequence.Value)
                .OrderBy(item => item.Sequence)
                .Take(128)
                .Select(item => new { item.EventId, item.Type })
                .ToListAsync(ct);
            var hasPending = await db.ExecutionRuns.AsNoTracking().AnyAsync(
                item => item.TurnId == iteration.TurnId
                    && (item.Status == "leased"
                        || item.Status == "running"
                        || item.Status == "cancel_requested"),
                ct);
            var expectedTerminalType = turn.TerminalKind switch
            {
                "completed" => ConversationEventTypes.TurnCompleted,
                "failed" => ConversationEventTypes.TurnFailed,
                "cancelled" => ConversationEventTypes.TurnCancelled,
                _ => string.Empty,
            };
            var refs = evidenceEvents
                .Select(item => $"conversation-event:{item.EventId}")
                .ToList();
            if (task is not null)
                refs.Add($"workspace-task:{task.TaskId}:version:{task.Version}:status:{task.Status}");

            results.Add(new GoalSettlementCandidate
            {
                GoalIterationId = iteration.GoalIterationId,
                GoalRunId = goal.GoalRunId,
                WorkspaceId = goal.WorkspaceId,
                ConversationId = goal.CurrentConversationId,
                AgentInstanceId = goal.AgentInstanceId,
                ActivationEpoch = iteration.ActivationEpoch,
                AggregateVersion = goal.AggregateVersion,
                IterationNo = iteration.IterationNo,
                MaxIterations = goal.MaxIterations,
                IterationsStarted = goal.IterationsStarted,
                Objective = goal.Objective,
                ObjectiveVersion = goal.ObjectiveVersion,
                TurnId = iteration.TurnId,
                TerminalKind = turn.TerminalKind!,
                TerminalSequence = turn.TerminalSequence.Value,
                EvidenceRefs = refs,
                TaskId = task?.TaskId,
                TaskVersion = task?.Version,
                TaskStatus = task?.Status.ToString(),
                TaskAcceptanceCriteria = task?.AcceptanceCriteria,
                HasPendingExecutionFacts = hasPending,
                EvidenceComplete = evidenceEvents.Any(item => item.Type == expectedTerminalType),
            });
        }

        return results;
    }

    public async Task<bool> ApplyAsync(
        GoalSettlementCandidate candidate,
        GoalVerificationDecision proposed,
        CancellationToken ct = default)
    {
        var nextContinuation = false;
        string? signalledConversation = null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var iteration = await db.GoalIterations.SingleOrDefaultAsync(
                item => item.GoalIterationId == candidate.GoalIterationId, ct);
            if (iteration is null || iteration.Status is not ("accepted" or "running"))
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var turn = await db.ConversationTurns.AsNoTracking().SingleOrDefaultAsync(
                item => item.TurnId == iteration.TurnId, ct);
            if (turn?.TerminalSequence is null
                || turn.TerminalSequence != candidate.TerminalSequence
                || turn.Status is not ("completed" or "failed" or "cancelled"))
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var goal = await db.GoalRuns.SingleOrDefaultAsync(
                item => item.GoalRunId == iteration.GoalRunId, ct);
            if (goal is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            var binding = await db.TaskGoalBindings.SingleOrDefaultAsync(
                item => item.GoalRunId == goal.GoalRunId, ct);
            WorkspaceTaskEntity? task = null;
            if (binding is not null)
            {
                task = await db.WorkspaceTasks.SingleOrDefaultAsync(
                    item => item.WorkspaceId == binding.WorkspaceId
                        && item.TaskId == binding.TaskId,
                    ct);
            }

            if (binding is not null
                && (binding.Status != "active"
                    || task is null
                    || task.TaskId != candidate.TaskId
                    || task.Version != candidate.TaskVersion))
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            var reservationValid = binding is null || await db.AgentExecutionReservations.AnyAsync(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.TaskId == binding.TaskId
                    && item.AgentId == binding.AgentInstanceId
                    && item.GoalRunId == binding.GoalRunId
                    && item.Status == "active", ct);

            var decision = ApplyDeterministicGates(proposed, task, candidate);
            if (!reservationValid)
            {
                decision = BlockedDecision(
                    "reservation_fence_lost",
                    "The Task-bound Goal reservation lease or fencing token is no longer authoritative.",
                    candidate.EvidenceRefs);
            }
            var now = DateTimeOffset.UtcNow;
            iteration.Status = turn.Status switch
            {
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => "settled",
            };
            iteration.TerminalSequence = turn.TerminalSequence;
            iteration.StopReason = turn.TerminalKind;
            iteration.SettledAtUtc = now;

            // accepted Iteration 永不退还预算；即使 epoch 已变化也要结算计数，
            // 但旧 epoch 绝不能创建 continuation 或改变当前 phase。
            if (goal.IterationsSettled < goal.IterationsStarted)
                goal.IterationsSettled++;
            goal.AggregateVersion++;
            goal.UpdatedAtUtc = now;

            var verificationId = $"gv-{goal.GoalRunId}-{iteration.ActivationEpoch}-{iteration.IterationNo}";
            db.GoalVerifications.Add(new GoalVerificationEntity
            {
                VerificationId = verificationId,
                GoalRunId = goal.GoalRunId,
                ActivationEpoch = iteration.ActivationEpoch,
                IterationNo = iteration.IterationNo,
                SourceTurnId = iteration.TurnId,
                SourceTerminalSequence = iteration.TerminalSequence,
                ContractVersion = 1,
                Status = "succeeded",
                Verdict = ToWire(decision.Verdict),
                Summary = decision.Reason,
                UnmetCriteriaJson = JsonSerializer.Serialize(decision.UnmetCriteria, JsonOpts),
                NextAction = decision.NextAction,
                BlockerCode = decision.BlockerCode,
                BlockerMessage = decision.BlockerMessage,
                EvidenceRefsJson = JsonSerializer.Serialize(decision.EvidenceRefs, JsonOpts),
                CreatedAtUtc = now,
                CompletedAtUtc = now,
            });
            goal.LastVerificationId = verificationId;
            goal.LastNextAction = decision.NextAction;
            goal.LastProgressFingerprint = decision.ProgressFingerprint;

            var events = new List<GoalEventDraft>
            {
                new(GoalEventTypes.IterationSettled, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    turnId = iteration.TurnId,
                    terminalSequence = iteration.TerminalSequence,
                    terminalKind = turn.TerminalKind,
                }),
                new(GoalEventTypes.VerificationRequested, GoalProducerComponents.Verifier, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    verificationId,
                }),
                new(GoalEventTypes.VerificationCompleted, GoalProducerComponents.Verifier, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    verificationId,
                    verdict = ToWire(decision.Verdict),
                    evidenceRefs = decision.EvidenceRefs,
                }),
            };

            var currentEpoch = goal.Status == GoalPhase.Active
                && goal.ActivationEpoch == iteration.ActivationEpoch;
            if (currentEpoch)
            {
                ApplyCurrentVerdict(db, goal, binding, task, iteration, decision, now, events, ref nextContinuation);
            }

            await AppendGoalEventsAsync(db, goal, iteration, events, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            signalledConversation = goal.CurrentConversationId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        if (signalledConversation is not null)
            committedSignal.Signal(signalledConversation, -1);
        if (nextContinuation)
            outboxSignal.Signal();
        return true;
    }

    private static GoalVerificationDecision ApplyDeterministicGates(
        GoalVerificationDecision proposed,
        WorkspaceTaskEntity? task,
        GoalSettlementCandidate candidate)
    {
        if (!candidate.EvidenceComplete || candidate.HasPendingExecutionFacts)
        {
            return BlockedDecision(
                "evidence_incomplete",
                "Canonical terminal evidence is incomplete or still pending.",
                candidate.EvidenceRefs);
        }
        if (!string.Equals(candidate.TerminalKind, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return BlockedDecision(
                $"iteration_{candidate.TerminalKind}",
                $"Iteration ended as {candidate.TerminalKind}.",
                candidate.EvidenceRefs);
        }
        if (task is not null
            && proposed.Verdict == GoalVerificationVerdict.Complete
            && task.Status != WorkspaceTaskStatus.Completed)
        {
            return new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = "Completion was rejected because the bound Task is not canonically Completed.",
                EvidenceRefs = candidate.EvidenceRefs,
                NextAction = "Use the task state tool to submit progress or evidence-backed completion.",
                UnmetCriteria = string.IsNullOrWhiteSpace(task.AcceptanceCriteria)
                    ? []
                    : [task.AcceptanceCriteria],
            };
        }
        return proposed with { EvidenceRefs = candidate.EvidenceRefs };
    }

    private void ApplyCurrentVerdict(
        PlatformDbContext db,
        GoalRunEntity goal,
        TaskGoalBindingEntity? binding,
        WorkspaceTaskEntity? task,
        GoalIterationEntity iteration,
        GoalVerificationDecision decision,
        DateTimeOffset now,
        List<GoalEventDraft> events,
        ref bool nextContinuation)
    {
        if (decision.Verdict == GoalVerificationVerdict.Complete)
        {
            goal.Status = GoalPhase.Completed;
            goal.StatusReason = decision.Reason;
            goal.TerminalAtUtc = now;
            goal.ActivationEpoch++;
            events.Add(new(GoalEventTypes.Completed, GoalProducerComponents.Coordinator, VerdictPayload(goal, iteration, decision)));
            if (binding is not null)
            {
                binding.Status = "terminal";
                binding.ReleasedAtUtc = now;
                events.Add(new(GoalEventTypes.TaskGoalCompleted, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    taskId = binding.TaskId,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                }));
                ReleaseReservation(db, binding, now, "goal_completed");
            }
            return;
        }

        if (decision.Verdict is GoalVerificationVerdict.Blocked
            or GoalVerificationVerdict.NeedsUser
            or GoalVerificationVerdict.Unsafe)
        {
            goal.Status = GoalPhase.Blocked;
            goal.BlockedCode = decision.BlockerCode ?? ToWire(decision.Verdict);
            goal.BlockedMessage = decision.BlockerMessage ?? decision.Reason;
            goal.StatusReason = decision.Reason;
            goal.ActivationEpoch++;
            events.Add(new(GoalEventTypes.Blocked, GoalProducerComponents.Coordinator, VerdictPayload(goal, iteration, decision)));
            if (binding is not null)
            {
                if (task is not null
                    && TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Blocked))
                {
                    task.Status = WorkspaceTaskStatus.Blocked;
                    task.BlockerKind = goal.BlockedCode;
                    task.BlockerReason = goal.BlockedMessage;
                    task.Version++;
                    task.UpdatedAtUtc = now;
                    AppendTaskEvent(db, task, binding, TaskEventType.TaskBlocked, now, goal.GoalRunId);
                }
                events.Add(new(GoalEventTypes.TaskGoalBlocked, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    taskId = binding.TaskId,
                    blockerCode = goal.BlockedCode,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                }));
                ReleaseReservation(db, binding, now, "goal_blocked");
            }
            return;
        }

        if (GoalStateMachine.IsBudgetExhausted(goal.MaxIterations, goal.IterationsStarted))
        {
            goal.Status = GoalPhase.BudgetExhausted;
            goal.StatusReason = "accepted_iteration_budget_exhausted";
            goal.TerminalAtUtc = now;
            goal.ActivationEpoch++;
            events.Add(new(GoalEventTypes.BudgetExhausted, GoalProducerComponents.Coordinator, VerdictPayload(goal, iteration, decision)));
            if (binding is not null)
            {
                binding.Status = "terminal";
                binding.ReleasedAtUtc = now;
                ReleaseReservation(db, binding, now, "goal_budget_exhausted");
            }
            return;
        }

        if (binding is not null && task is not null)
        {
            // Freeze the exact Task revision that the next synthetic acceptance
            // must revalidate. Task tool writes made during this iteration are
            // therefore included, while later external edits fence the outbox.
            binding.ExpectedTaskVersion = task.Version;
            RenewReservation(db, binding, now);
        }

        var nextIteration = goal.IterationsStarted + 1;
        var outboxId = $"gc-{goal.GoalRunId}-{goal.ActivationEpoch}-{nextIteration}";
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
                taskId = binding?.TaskId,
                expectedTaskVersion = binding?.ExpectedTaskVersion,
                reservationFencingToken = binding?.ReservationFencingToken,
            }, JsonOpts),
            Status = GoalOutboxValues.Pending,
            DueAtUtc = now,
            CreatedAtUtc = now,
        });
        events.Add(new(GoalEventTypes.ContinuationRequested, GoalProducerComponents.Continuation, new
        {
            goalRunId = goal.GoalRunId,
            activationEpoch = goal.ActivationEpoch,
            aggregateVersion = goal.AggregateVersion,
            iterationNumber = nextIteration,
            remainingIterations = goal.MaxIterations - goal.IterationsStarted,
        }));
        nextContinuation = true;
    }

    private static void ReleaseReservation(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        DateTimeOffset now,
        string reason)
    {
        if (binding.ReservationId is null || binding.ReservationFencingToken is null)
            return;
        var reservation = db.AgentExecutionReservations.Local.FirstOrDefault(
            item => item.ReservationId == binding.ReservationId)
            ?? db.AgentExecutionReservations.SingleOrDefault(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.Status == "active");
        if (reservation is null)
            return;
        reservation.Status = "released";
        reservation.ReleaseReason = reason;
        reservation.ReleasedAtUtc = now;
        reservation.UpdatedAtUtc = now;
    }

    private void RenewReservation(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        DateTimeOffset now)
    {
        if (binding.ReservationId is null || binding.ReservationFencingToken is null)
            return;
        var reservation = db.AgentExecutionReservations.Local.FirstOrDefault(
            item => item.ReservationId == binding.ReservationId)
            ?? db.AgentExecutionReservations.SingleOrDefault(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.Status == "active");
        if (reservation is null)
            return;
        reservation.LeaseUntilUtc = now.Add(_reservationLease);
        reservation.UpdatedAtUtc = now;
    }

    private static void AppendTaskEvent(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        TaskGoalBindingEntity binding,
        TaskEventType eventType,
        DateTimeOffset now,
        string goalRunId)
    {
        var next = (db.TaskEvents
            .Where(item => item.TaskId == task.TaskId)
            .Max(item => (long?)item.Sequence) ?? 0) + 1;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = $"tgb-{task.TaskId}-{task.Version}",
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = next,
            EventType = eventType,
            AssignmentId = binding.AssignmentId,
            AgentId = binding.AgentInstanceId,
            SessionId = binding.GoalRunId,
            CorrelationId = goalRunId,
            CausationId = binding.BindingId,
            CreatedAtUtc = now,
        });
    }

    private static async Task AppendGoalEventsAsync(
        PlatformDbContext db,
        GoalRunEntity goal,
        GoalIterationEntity iteration,
        IReadOnlyList<GoalEventDraft> drafts,
        CancellationToken ct)
    {
        var head = await db.ConversationHeads.SingleOrDefaultAsync(
            item => item.ConversationId == goal.CurrentConversationId, ct);
        var previous = head?.HeadSequence ?? 0;
        if (head is null)
        {
            head = new ConversationHeadEntity { ConversationId = goal.CurrentConversationId };
            db.ConversationHeads.Add(head);
        }
        head.HeadSequence = previous + drafts.Count;

        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = previous + index + 1,
                EventId = $"gs{index}-{iteration.GoalIterationId}-{goal.AggregateVersion}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = iteration.TurnId ?? string.Empty,
                CommandId = iteration.CommandId,
                RunId = iteration.RunId,
                Type = draft.EventType,
                SchemaVersion = 1,
                Payload = JsonSerializer.Serialize(draft.Payload, JsonOpts),
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = goal.GoalRunId,
                CausationId = iteration.TurnId,
                AgentId = goal.AgentInstanceId,
                SourceKind = "goal",
                TraceId = iteration.TraceId,
                ProducerComponent = draft.ProducerComponent,
            });
        }
    }

    private static object VerdictPayload(
        GoalRunEntity goal,
        GoalIterationEntity iteration,
        GoalVerificationDecision decision) => new
    {
        goalRunId = goal.GoalRunId,
        activationEpoch = goal.ActivationEpoch,
        aggregateVersion = goal.AggregateVersion,
        iterationNumber = iteration.IterationNo,
        verdict = ToWire(decision.Verdict),
        reason = decision.Reason,
        blockerCode = decision.BlockerCode,
    };

    private static GoalVerificationDecision BlockedDecision(
        string code,
        string message,
        IReadOnlyList<string> refs) => new()
    {
        Verdict = GoalVerificationVerdict.Blocked,
        Reason = message,
        EvidenceRefs = refs,
        BlockerCode = code,
        BlockerMessage = message,
        NextAction = "Resolve the blocker, then explicitly resume the Goal.",
    };

    private static string ToWire(GoalVerificationVerdict verdict) => verdict switch
    {
        GoalVerificationVerdict.Continue => "continue",
        GoalVerificationVerdict.Complete => "complete",
        GoalVerificationVerdict.Blocked => "blocked",
        GoalVerificationVerdict.NeedsUser => "needs_user",
        GoalVerificationVerdict.Unsafe => "unsafe",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private sealed record GoalEventDraft(string EventType, string ProducerComponent, object Payload);
}
