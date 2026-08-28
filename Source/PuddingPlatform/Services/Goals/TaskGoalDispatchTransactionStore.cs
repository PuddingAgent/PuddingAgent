using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// The only authoritative Task -> Goal startup writer. Task, Assignment,
/// Reservation, Binding, Goal and first continuation intent commit together;
/// no network, model, tool or message send occurs inside the transaction.
/// </summary>
public sealed class TaskGoalDispatchTransactionStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ICommittedEventSignal committedSignal,
    GoalOutboxSignal outboxSignal,
    TimeProvider timeProvider,
    IWorkspaceAgentCatalog agentCatalog,
    IOptions<TaskAutoDispatchOptions> dispatchOptions,
    ILogger<TaskGoalDispatchTransactionStore> logger)
    : ITaskGoalDispatchTransactionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<TaskBoundGoalStartResult> StartAsync(
        StartGoalFromTaskCommand command,
        CancellationToken ct = default)
    {
        ValidateCommand(command);
        var now = timeProvider.GetUtcNow();
        var agentSnapshot = (await agentCatalog.ListAgentsAsync(command.WorkspaceId, ct))
            .SingleOrDefault(agent => string.Equals(agent.AgentId, command.AgentId, StringComparison.Ordinal));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var replay = await db.TaskGoalBindings.AsNoTracking().SingleOrDefaultAsync(
                item => item.IdempotencyKey == command.IdempotencyKey, ct);
            if (replay is not null)
            {
                if (!string.Equals(
                        replay.PlanFingerprint,
                        command.ExpectedExecutionPlanFingerprint,
                        StringComparison.Ordinal))
                {
                    return await RejectAsync(tx, TaskBoundGoalStartCodes.PlanChanged, ct,
                        replay.ExpectedTaskVersion);
                }
                var replayReservation = replay.ReservationId is null
                    ? null
                    : await db.AgentExecutionReservations.AsNoTracking().SingleOrDefaultAsync(
                        item => item.ReservationId == replay.ReservationId, ct);
                await tx.CommitAsync(ct);
                return Result(true, TaskBoundGoalStartCodes.IdempotentReplay, replay.GoalRunId,
                    replay.AssignmentId, replay.ReservationId, replayReservation?.FencingToken,
                    replay.ExpectedTaskVersion, replay.TaskPlanId, replay.PlanFingerprint);
            }

            var task = await db.WorkspaceTasks.SingleOrDefaultAsync(
                item => item.WorkspaceId == command.WorkspaceId && item.TaskId == command.TaskId, ct);
            if (task is null)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.TaskMissing, ct);
            if (task.Version != command.ExpectedTaskVersion)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.TaskChanged, ct, task.Version);
            if (task.Status is not (WorkspaceTaskStatus.Ready or WorkspaceTaskStatus.Deferred)
                || task.ActiveAssignmentId is not null
                || !task.AutoDispatchEnabled)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.TaskNotEligible, ct, task.Version);
            if (!task.AllowAgentFallback
                && !string.IsNullOrWhiteSpace(task.PreferredAgentId)
                && !string.Equals(task.PreferredAgentId, command.AgentId, StringComparison.Ordinal))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentChanged, ct, task.Version);
            if (!IsSha256(command.ExpectedAgentRoutingFingerprint))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentChanged, ct, task.Version);
            dispatchOptions.Value.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);
            if (agentSnapshot is null)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentChanged, ct, task.Version);
            var route = TaskAgentRouteMatcher.Evaluate(task, agentSnapshot, typeRoute);
            if (!route.Compatible
                || !string.Equals(route.Fingerprint, command.ExpectedAgentRoutingFingerprint, StringComparison.Ordinal))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentChanged, ct, task.Version);
            if (!TaskExecutionPlanCompiler.TryCompile(task, typeRoute, out var executionPlan, out _)
                || !string.Equals(
                    executionPlan!.Fingerprint,
                    command.ExpectedExecutionPlanFingerprint,
                    StringComparison.Ordinal))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.PlanChanged, ct, task.Version);
            if (task.ExecutionWindow != command.ExecutionWindow)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.WindowChanged, ct, task.Version);
            if (Later(task.NotBeforeUtc, task.NextEligibleAtUtc) > now)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.TaskNotEligible, ct, task.Version);

            if (!await DependenciesStillSatisfiedAsync(db, command.WorkspaceId, command.TaskId, ct))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.DependencyChanged, ct, task.Version);

            var availability = await db.AgentAvailabilityProjections.SingleOrDefaultAsync(
                item => item.WorkspaceId == command.WorkspaceId && item.AgentId == command.AgentId, ct);
            if (availability is null || availability.Version != command.ExpectedAvailabilityVersion)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentChanged, ct, task.Version);
            if (availability.State != AgentAvailabilityState.Idle
                || availability.ActivityReason != AgentActivityReason.None
                || availability.ValidUntilUtc <= now
                || availability.IdleSinceUtc is null
                || availability.IdleSinceUtc.Value.Add(command.MinimumIdle) > now
                || availability.ActiveTurnId is not null
                || availability.ActiveExecutionId is not null
                || availability.ActiveTaskId is not null
                || availability.ActiveGoalRunId is not null
                || availability.ActiveSubAgentRunId is not null
                || availability.ReservationId is not null)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentNotIdle, ct, task.Version);
            if (string.IsNullOrWhiteSpace(availability.MainConversationId)
                || !string.Equals(availability.MainConversationId, command.ConversationId, StringComparison.Ordinal))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.ConversationMissing, ct, task.Version);

            if (command.WindowDecision.Verdict != ExecutionWindowVerdict.Allow)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.WindowChanged, ct, task.Version);
            if (command.WindowDecision.ValidUntilUtc <= now
                || command.WindowDecision.EvaluatedAtUtc > now)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.WindowExpired, ct, task.Version);

            var conversationBusy = await db.ChatExecutionCommands.AnyAsync(
                item => item.SessionId == command.ConversationId
                    && (item.Status == "pending" || item.Status == "leased"
                        || item.Status == "running" || item.Status == "cancel_requested"), ct);
            var activeBinding = await db.TaskGoalBindings.AnyAsync(
                item => item.WorkspaceId == command.WorkspaceId
                    && item.AgentInstanceId == command.AgentId && item.Status == "active", ct);
            var activeAssignment = await (
                    from attempt in db.TaskAssignmentAttempts
                    join ownedTask in db.WorkspaceTasks
                        on new { attempt.WorkspaceId, attempt.TaskId }
                        equals new { ownedTask.WorkspaceId, ownedTask.TaskId }
                    where attempt.WorkspaceId == command.WorkspaceId
                        && attempt.AgentId == command.AgentId
                        && attempt.ReleasedAtUtc == null
                        && ownedTask.ActiveAssignmentId == attempt.AttemptId
                        && ownedTask.Status != WorkspaceTaskStatus.Completed
                        && ownedTask.Status != WorkspaceTaskStatus.Failed
                        && ownedTask.Status != WorkspaceTaskStatus.Cancelled
                        && ownedTask.Status != WorkspaceTaskStatus.Archived
                    select attempt)
                .AnyAsync(ct);
            if (conversationBusy || activeBinding || activeAssignment)
                return await RejectAsync(tx, TaskBoundGoalStartCodes.AgentNotIdle, ct, task.Version);

            var expiredReservations = await db.AgentExecutionReservations
                .Where(item => item.Status == "active"
                    && item.WorkspaceId == command.WorkspaceId
                    && (item.AgentId == command.AgentId || item.TaskId == command.TaskId))
                .ToListAsync(ct);
            foreach (var expired in expiredReservations.Where(item => item.LeaseUntilUtc <= now))
            {
                expired.Status = "expired";
                expired.ReleaseReason = "lease_expired_before_task_goal_start";
                expired.ReleasedAtUtc = now;
                expired.UpdatedAtUtc = now;
            }
            if (expiredReservations.Any(item => item.LeaseUntilUtc > now))
                return await RejectAsync(tx, TaskBoundGoalStartCodes.LostRace, ct, task.Version);

            var assignmentId = Guid.NewGuid().ToString("N");
            var reservationId = Guid.NewGuid().ToString("N");
            var bindingId = Guid.NewGuid().ToString("N");
            var goalRunId = BuildGoalRunId(command.IdempotencyKey);
            var taskPlanId = BuildTaskPlanId(task, executionPlan!);
            var attemptNo = (await db.TaskAssignmentAttempts
                .Where(item => item.TaskId == task.TaskId)
                .MaxAsync(item => (int?)item.AttemptNumber, ct) ?? 0) + 1;

            var assignment = new TaskAssignmentAttemptEntity
            {
                AttemptId = assignmentId,
                TaskId = task.TaskId,
                WorkspaceId = task.WorkspaceId,
                AgentId = command.AgentId,
                AttemptNumber = attemptNo,
                Status = AssignmentAttemptStatus.Assigned,
                WindowDecision = JsonSerializer.Serialize(command.WindowDecision, JsonOpts),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ActiveAtUtc = now,
            };
            var reservation = new AgentExecutionReservationEntity
            {
                ReservationId = reservationId,
                WorkspaceId = task.WorkspaceId,
                AgentId = command.AgentId,
                TaskId = task.TaskId,
                GoalRunId = goalRunId,
                OwnerId = command.OwnerId,
                Status = "active",
                LeaseUntilUtc = now.Add(command.ReservationLease),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            task.Status = WorkspaceTaskStatus.Assigned;
            task.ActiveAssignmentId = assignmentId;
            // The atomic command embodies Ready/Deferred -> Reserved -> Assigned.
            task.Version += 2;
            task.UpdatedAtUtc = now;
            task.UpdatedBy = "task-auto-dispatch";
            db.TaskAssignmentAttempts.Add(assignment);
            db.AgentExecutionReservations.Add(reservation);
            AddExecutionPlan(db, task, executionPlan!, taskPlanId, command, now);
            await db.SaveChangesAsync(ct); // obtains the SQLite identity fencing token; still inside this transaction.

            var windowSnapshot = JsonSerializer.Serialize(new
            {
                command.ExecutionWindow,
                decision = command.WindowDecision,
                agentRoutingFingerprint = command.ExpectedAgentRoutingFingerprint,
                executionPlanFingerprint = executionPlan!.Fingerprint,
                executionPlanSchemaVersion = executionPlan.SchemaVersion,
                executionPlanVersion = executionPlan.PlanVersion,
                taskPlanId,
                task.TaskType,
                task.RequiredCapabilitiesJson,
                task.RequiredProviderId,
                task.RequiredModelId,
                task.AllowAgentFallback,
                task.AutoDispatchEnabled,
                availabilityVersion = command.ExpectedAvailabilityVersion,
                reservationLeaseSeconds = (long)command.ReservationLease.TotalSeconds,
            }, JsonOpts);
            db.TaskGoalBindings.Add(new TaskGoalBindingEntity
            {
                BindingId = bindingId,
                WorkspaceId = task.WorkspaceId,
                TaskId = task.TaskId,
                AssignmentId = assignmentId,
                ExpectedTaskVersion = task.Version,
                GoalRunId = goalRunId,
                AgentInstanceId = command.AgentId,
                ReservationId = reservationId,
                ReservationFencingToken = reservation.FencingToken,
                TaskPlanId = taskPlanId,
                PlanFingerprint = executionPlan.Fingerprint,
                ExecutionWindowSnapshotJson = windowSnapshot,
                Status = "active",
                IdempotencyKey = command.IdempotencyKey,
                CreatedAtUtc = now,
            });

            var goal = new GoalRunEntity
            {
                GoalRunId = goalRunId,
                WorkspaceId = task.WorkspaceId,
                CurrentConversationId = command.ConversationId,
                AgentInstanceId = command.AgentId,
                Objective = BuildObjective(task),
                ObjectiveVersion = 1,
                Status = GoalPhase.Active,
                MaxIterations = command.GoalIterationBudget,
                ActivationEpoch = 1,
                AggregateVersion = 1,
                SourceChannel = "workspace_task",
                SourceCommandId = BuildSourceCommandId(command.IdempotencyKey),
                RouteSnapshotJson = windowSnapshot,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.GoalRuns.Add(goal);
            var outboxId = $"gc-{goalRunId}-1-1";
            db.GoalOutbox.Add(new GoalOutboxEntity
            {
                OutboxId = outboxId,
                GoalRunId = goalRunId,
                ActivationEpoch = 1,
                AggregateVersion = 1,
                Kind = GoalOutboxValues.Continuation,
                IdempotencyKey = outboxId,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    goalRunId,
                    objectiveVersion = 1,
                    iterationNo = 1,
                    taskId = task.TaskId,
                    assignmentId,
                    reservationFencingToken = reservation.FencingToken,
                    taskPlanId,
                    executionPlanFingerprint = executionPlan.Fingerprint,
                }, JsonOpts),
                Status = GoalOutboxValues.Pending,
                DueAtUtc = now,
                CreatedAtUtc = now,
            });

            availability.State = AgentAvailabilityState.Reserved;
            availability.ActivityReason = AgentActivityReason.ActiveReservation;
            availability.Version++;
            availability.ObservedAtUtc = now;
            availability.ValidUntilUtc = reservation.LeaseUntilUtc;
            availability.IdleSinceUtc = null;
            availability.ActiveTaskId = task.TaskId;
            availability.ActiveGoalRunId = goalRunId;
            availability.ReservationId = reservationId;
            availability.ReasonCode = "active_auto_reservation";

            await AppendTaskEventsAsync(db, task, assignmentId, command.AgentId,
                command.ConversationId, command, now, ct);
            await AppendGoalEventsAsync(db, goal, task, assignmentId, reservation, bindingId,
                taskPlanId, command, now, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            committedSignal.Signal(command.ConversationId, -1);
            outboxSignal.Signal();
            logger.LogInformation(
                "[TaskGoalDispatch] started workspace={WorkspaceId} task={TaskId} taskVersion={TaskVersion} agent={AgentId} goal={GoalRunId} assignment={AssignmentId} reservation={ReservationId} fence={Fence} plan={TaskPlanId} planFingerprint={PlanFingerprint}",
                command.WorkspaceId, command.TaskId, task.Version, command.AgentId, goalRunId,
                assignmentId, reservationId, reservation.FencingToken, taskPlanId, executionPlan.Fingerprint);
            return Result(true, TaskBoundGoalStartCodes.Started, goalRunId, assignmentId,
                reservationId, reservation.FencingToken, task.Version, taskPlanId,
                executionPlan.Fingerprint);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex,
                "[TaskGoalDispatch] lost race workspace={WorkspaceId} task={TaskId} agent={AgentId}",
                command.WorkspaceId, command.TaskId, command.AgentId);
            return Result(false, TaskBoundGoalStartCodes.LostRace);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex,
                "[TaskGoalDispatch] SQLite writer race workspace={WorkspaceId} task={TaskId} agent={AgentId}",
                command.WorkspaceId, command.TaskId, command.AgentId);
            return Result(false, TaskBoundGoalStartCodes.LostRace);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void AddExecutionPlan(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        TaskExecutionPlanSnapshot plan,
        string taskPlanId,
        StartGoalFromTaskCommand command,
        DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var rootNodeId = $"tn-{Hash($"{taskPlanId}:root")[..32]}";
        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = taskPlanId,
            WorkspaceId = task.WorkspaceId,
            WorkspaceTaskId = task.TaskId,
            WorkspaceTaskVersion = plan.TaskVersion,
            PlanVersion = plan.PlanVersion,
            SchemaVersion = plan.SchemaVersion,
            PlanKind = plan.PlanKind,
            PlanFingerprint = plan.Fingerprint,
            RootSessionId = command.ConversationId,
            LeaderAgentId = command.AgentId,
            Objective = task.Title.Length <= 1024 ? task.Title : task.Title[..1024],
            Status = TaskPlanStatuses.Active.ToString(),
            MaxDelegationDepth = 1,
            DefaultAllowSubDelegation = false,
            AllowAgentCreationByLeader = false,
            MaxActiveTaskNodesPerPlan = plan.WorkUnits.Count + 1,
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
            TraceId = command.CorrelationId,
            CorrelationId = command.CorrelationId,
        });
        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = rootNodeId,
            PlanId = taskPlanId,
            Depth = 0,
            SequenceNo = 0,
            Title = task.Title.Length <= 256 ? task.Title : task.Title[..256],
            Objective = "Execute the frozen WorkspaceTask plan and produce completion proof.",
            AssignedToKind = TaskAssignmentKinds.Leader.ToString(),
            AssignedToId = command.AgentId,
            CreatedByAgentId = command.AgentId,
            Status = TaskNodeStatuses.Running.ToString(),
            AllowSubDelegation = false,
            AllowAgentCreation = false,
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
        });

        foreach (var unit in plan.WorkUnits)
        {
            db.TaskNodes.Add(new TaskNodeEntity
            {
                TaskNodeId = $"tn-{Hash($"{taskPlanId}:{unit.WorkUnitId}")[..32]}",
                PlanId = taskPlanId,
                ParentTaskNodeId = rootNodeId,
                Depth = 1,
                SequenceNo = unit.Sequence,
                WorkUnitKind = unit.Kind.ToString(),
                DependsOnJson = JsonSerializer.Serialize(unit.DependsOn, JsonOpts),
                ScopeJson = JsonSerializer.Serialize(unit.ConflictScopes, JsonOpts),
                RequiredCapabilityIdsJson = JsonSerializer.Serialize(unit.RequiredCapabilityIds, JsonOpts),
                MaxRounds = unit.Budget.MaxRounds,
                MaxToolCalls = unit.Budget.MaxToolCalls,
                MaxDurationSeconds = unit.Budget.MaxDurationSeconds,
                MaxInputTokens = unit.Budget.MaxInputTokens,
                MaxOutputTokens = unit.Budget.MaxOutputTokens,
                MaxCost = unit.Budget.MaxCost,
                RetryPolicyJson = JsonSerializer.Serialize(new { policy = unit.RetryPolicy }, JsonOpts),
                Title = unit.Kind.ToString(),
                Objective = unit.Objective,
                ExpectedOutputContract = "checkpoint-v1 with evidence refs, progress fingerprint and terminal verdict",
                AssignedToKind = TaskAssignmentKinds.Leader.ToString(),
                AssignedToId = command.AgentId,
                CreatedByAgentId = command.AgentId,
                Status = unit.Sequence == 1
                    ? TaskNodeStatuses.Planned.ToString()
                    : TaskNodeStatuses.Draft.ToString(),
                AllowSubDelegation = false,
                AllowAgentCreation = false,
                CreatedAt = nowMs,
                UpdatedAt = nowMs,
            });
        }
    }

    private static async Task<bool> DependenciesStillSatisfiedAsync(
        PlatformDbContext db, string workspaceId, string taskId, CancellationToken ct)
    {
        var predecessorIds = await db.TaskDependencies.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.SuccessorTaskId == taskId)
            .Select(item => item.PredecessorTaskId)
            .ToListAsync(ct);
        if (predecessorIds.Count == 0)
            return true;
        var states = await db.WorkspaceTasks.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && predecessorIds.Contains(item.TaskId))
            .Select(item => new { item.TaskId, item.Status })
            .ToListAsync(ct);
        return states.Count == predecessorIds.Distinct(StringComparer.Ordinal).Count()
            && states.All(item => item.Status == WorkspaceTaskStatus.Completed);
    }

    private static async Task AppendTaskEventsAsync(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        string assignmentId,
        string agentId,
        string conversationId,
        StartGoalFromTaskCommand command,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var sequence = (await db.TaskEvents.Where(item => item.TaskId == task.TaskId)
            .MaxAsync(item => (long?)item.Sequence, ct) ?? 0) + 1;
        db.TaskEvents.AddRange(
            BuildTaskEvent($"tgr-{assignmentId}", TaskEventType.TaskReserved, sequence),
            BuildTaskEvent($"tga-{assignmentId}", TaskEventType.TaskAssigned, sequence + 1));

        TaskEventEntity BuildTaskEvent(string eventId, TaskEventType type, long seq) => new()
        {
            EventId = eventId,
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = seq,
            EventType = type,
            AssignmentId = assignmentId,
            AgentId = agentId,
            SessionId = conversationId,
            Origin = task.Origin,
            Priority = task.Priority,
            DecisionCode = command.WindowDecision.Code,
            CorrelationId = command.CorrelationId,
            CausationId = command.CausationId,
            CreatedAtUtc = now,
        };
    }

    private static async Task AppendGoalEventsAsync(
        PlatformDbContext db,
        GoalRunEntity goal,
        WorkspaceTaskEntity task,
        string assignmentId,
        AgentExecutionReservationEntity reservation,
        string bindingId,
        string taskPlanId,
        StartGoalFromTaskCommand command,
        DateTimeOffset now,
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

        var drafts = new (string Type, object Payload)[]
        {
            (GoalEventTypes.Created, GoalPayload()),
            (GoalEventTypes.Activated, GoalPayload()),
            (GoalEventTypes.TaskGoalBound, new
            {
                goalRunId = goal.GoalRunId,
                taskId = task.TaskId,
                taskVersion = task.Version,
                assignmentId,
                bindingId,
                reservationId = reservation.ReservationId,
                reservationFencingToken = reservation.FencingToken,
                taskPlanId,
                executionPlanFingerprint = command.ExpectedExecutionPlanFingerprint,
            }),
            (GoalEventTypes.ContinuationRequested, new
            {
                goalRunId = goal.GoalRunId,
                activationEpoch = 1,
                aggregateVersion = 1,
                iterationNumber = 1,
                remainingIterations = goal.MaxIterations,
            }),
        };
        head.HeadSequence = previous + drafts.Length;
        for (var index = 0; index < drafts.Length; index++)
        {
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = previous + index + 1,
                EventId = $"tgs{index}-{goal.GoalRunId}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = string.Empty,
                CommandId = goal.SourceCommandId,
                Type = drafts[index].Type,
                SchemaVersion = 1,
                Payload = JsonSerializer.Serialize(drafts[index].Payload, JsonOpts),
                OccurredAt = now.ToString("O"),
                CommittedAt = now.ToString("O"),
                CorrelationId = command.CorrelationId,
                CausationId = command.CausationId,
                AgentId = goal.AgentInstanceId,
                SourceKind = "goal",
                TraceId = command.CorrelationId,
                ProducerComponent = GoalProducerComponents.Coordinator,
            });
        }

        object GoalPayload() => new
        {
            goalRunId = goal.GoalRunId,
            workspaceId = goal.WorkspaceId,
            conversationId = goal.CurrentConversationId,
            agentInstanceId = goal.AgentInstanceId,
            status = "active",
            objectiveVersion = goal.ObjectiveVersion,
            maxIterations = goal.MaxIterations,
            activationEpoch = goal.ActivationEpoch,
            aggregateVersion = goal.AggregateVersion,
            sourceChannel = goal.SourceChannel,
            taskId = task.TaskId,
            assignmentId,
            taskPlanId,
            executionPlanFingerprint = command.ExpectedExecutionPlanFingerprint,
        };
    }

    private static string BuildObjective(WorkspaceTaskEntity task)
    {
        var value = $"Workspace Task: {task.Title.Trim()}\n\n" +
                    $"Description:\n{task.Description?.Trim() ?? "(none)"}\n\n" +
                    $"Acceptance criteria:\n{task.AcceptanceCriteria?.Trim() ?? "(none)"}";
        return value.Length <= GoalLimits.ObjectiveMaxLength
            ? value
            : value[..GoalLimits.ObjectiveMaxLength];
    }

    private static string BuildGoalRunId(string key) => $"tg-{Hash(key)[..32]}";
    private static string BuildTaskPlanId(WorkspaceTaskEntity task, TaskExecutionPlanSnapshot plan)
        => $"tp-{Hash($"{task.WorkspaceId}:{task.TaskId}:{task.Version}:{plan.Fingerprint}")[..32]}";
    private static string BuildSourceCommandId(string key) => $"task-goal-{Hash(key)[..48]}";
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
        => first is null ? second : second is null ? first : first > second ? first : second;

    private static async Task<TaskBoundGoalStartResult> RejectAsync(
        IDbContextTransaction tx, string code, CancellationToken ct, int? taskVersion = null)
    {
        await tx.RollbackAsync(ct);
        return Result(false, code, taskVersion: taskVersion);
    }

    private static TaskBoundGoalStartResult Result(
        bool started, string code, string? goalRunId = null, string? assignmentId = null,
        string? reservationId = null, long? fence = null, int? taskVersion = null,
        string? taskPlanId = null, string? executionPlanFingerprint = null) => new()
    {
        Started = started,
        Code = code,
        GoalRunId = goalRunId,
        AssignmentId = assignmentId,
        ReservationId = reservationId,
        ReservationFencingToken = fence,
        TaskVersion = taskVersion,
        TaskPlanId = taskPlanId,
        ExecutionPlanFingerprint = executionPlanFingerprint,
    };

    private static void ValidateCommand(StartGoalFromTaskCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AgentId);
        if (!IsSha256(command.ExpectedAgentRoutingFingerprint))
            throw new ArgumentException("ExpectedAgentRoutingFingerprint must be a lowercase SHA-256 hex value.", nameof(command));
        if (!IsSha256(command.ExpectedExecutionPlanFingerprint))
            throw new ArgumentException("ExpectedExecutionPlanFingerprint must be a lowercase SHA-256 hex value.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);
        if (command.ExpectedTaskVersion < 1 || command.ExpectedAvailabilityVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(command));
        if (!GoalLimits.IsValidIterationBudget(command.GoalIterationBudget))
            throw new ArgumentOutOfRangeException(nameof(command.GoalIterationBudget));
        if (command.MinimumIdle < TimeSpan.Zero || command.ReservationLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
