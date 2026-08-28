using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-059: 原子受理存储 — 使用 PlatformDbContext 单事务写入 Message + Batch + Commands + Events + Head。
/// Scoped 服务，不创建新 Scope 或跨连接 Store 调用。
/// </summary>
public sealed class ConversationAcceptanceStore(
    PlatformDbContext db,
    ICommittedEventSignal committedSignal,
    ILogger<ConversationAcceptanceStore> logger,
    IOptions<TaskBoundGoalOptions>? taskBoundOptions = null,
    TimeProvider? timeProvider = null) : IConversationAcceptanceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _taskBoundReservationLease =
        taskBoundOptions?.Value.ReservationLease ?? TimeSpan.FromHours(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AcceptanceResult> AcceptBatchAsync(
        SubmitTurnRequest request,
        string workspaceId,
        string conversationId,
        string? userId,
        CancellationToken ct)
    {
        // ── Step 1: 幂等检查 — 按 (workspace_id, client_request_id) 查已存在批次 ──
        var existingBatch = await db.AcceptanceBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b =>
                b.WorkspaceId == workspaceId &&
                b.ClientRequestId == request.ClientRequestId, ct);

        if (existingBatch is not null)
        {
            var existingCommands = await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(c => c.BatchId == existingBatch.BatchId)
                .ToListAsync(ct);

            logger.LogInformation(
                "[AcceptStore] Idempotent hit batch={BatchId} conv={ConvId} cmds={Count} seq={Seq}",
                existingBatch.BatchId, existingBatch.ConversationId,
                existingCommands.Count, existingBatch.AcceptedSequence);

            return new AcceptanceResult
            {
                ConversationId = existingBatch.ConversationId,
                MessageId = existingBatch.MessageId,
                TurnIds = existingCommands.Select(c => c.TurnId).ToList(),
                CommandIds = existingCommands.Select(c => c.CommandId).ToList(),
                AcceptedSequence = existingBatch.AcceptedSequence,
            };
        }

        // ── Step 2: 单事务写入全部事实 ──
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var batchId = Guid.NewGuid().ToString("N");
            var agentIds = request.Recipients.AgentIds
                ?? throw new InvalidOperationException(
                    "Validated turn acceptance requires explicit agent IDs.");
            var goalContinuation = request.GoalContinuation;
            (GoalRunEntity Goal, GoalOutboxEntity Outbox)? goalFence = goalContinuation is null
                ? null
                : await ValidateGoalContinuationAsync(
                    goalContinuation,
                    workspaceId,
                    conversationId,
                    agentIds,
                    ct);
            // ADR-077 §4.2：Content 为全部 text part 的稳定拼接投影；ContentPartsJson 才是多模态 canonical fact。
            var contentParts = ConversationContentValidator.ToLlmContentParts(
                request.Content,
                out var textContent);
            var contentPartsJson = contentParts is null ? null : ContentPartsEnvelope.Encode(contentParts);
            var nowUtc = _timeProvider.GetUtcNow();
            var now = nowUtc.ToUnixTimeMilliseconds();

            // 2a: 用户消息
            var message = new ChatMessageEntity
            {
                MessageId = request.ClientMessageId,
                SessionId = conversationId,
                WorkspaceId = workspaceId,
                Role = "user",
                Content = textContent,
                UserId = userId,
                CreatedAt = now,
                MetadataJson = SerializeMetadata(request.Metadata),
                ContentPartsJson = contentPartsJson,
            };
            db.ChatMessages.Add(message);

            // 2b: 受理批次
            var batch = new AcceptanceBatchEntity
            {
                BatchId = batchId,
                WorkspaceId = workspaceId,
                ClientRequestId = request.ClientRequestId,
                ConversationId = conversationId,
                MessageId = request.ClientMessageId,
                Status = "accepted",
                TurnCount = agentIds.Count,
                UserId = userId,
                CreatedAt = now,
            };
            db.AcceptanceBatches.Add(batch);

            // 2c: 为每个 Agent 创建执行命令
            var commands = new List<ChatExecutionCommandEntity>();
            foreach (var agentId in agentIds)
            {
                var turnId = Guid.NewGuid().ToString("N");
                var commandId = Guid.NewGuid().ToString("N");
                var traceId = Guid.NewGuid().ToString("N");
                commands.Add(new ChatExecutionCommandEntity
                {
                    BatchId = batchId,
                    CommandId = commandId,
                    ClientRequestId = request.ClientRequestId,
                    WorkspaceId = workspaceId,
                    SessionId = conversationId,
                    MessageId = Guid.NewGuid().ToString("N"),
                    UserMessageId = request.ClientMessageId,
                    TurnId = turnId,
                    AgentInstanceId = agentId,
                    UserId = userId,
                    ChannelId = GetMetadataValue(request.Metadata, MessageGatewayMetadata.ChannelId),
                    Status = "pending",
                    TraceId = traceId,
                    CreatedAt = now,
                    MetadataJson = SerializeMetadata(request.Metadata),
                });
            }
            db.ChatExecutionCommands.AddRange(commands);

            // 2d: 为每个 Command 分配 Event Store sequence 并写 turn.accepted
            var goalEventCount = goalFence is null ? 0 : 2;
            var eventCount = commands.Count + goalEventCount;
            var headSeq = await AllocateSequencesAsync(conversationId, eventCount, ct);
            var events = new List<ConversationEventEntity>();
            for (int i = 0; i < commands.Count; i++)
            {
                var seq = headSeq + i + 1;
                var cmd = commands[i];
                // ADR-077 §4.2：事件消费者需要立即显示附件时，只携带 type/artifactId/detail 安全摘要，
                // 不携带字节、路径或 Provider file_id。
                object? attachments = contentParts is null
                    ? null
                    : contentParts
                        .OfType<PuddingCode.Models.LlmImagePart>()
                        .Select(image => new
                        {
                            type = "image",
                            artifactId = image.ArtifactId,
                            detail = image.Detail,
                        })
                        .ToArray();
                var payload = JsonSerializer.SerializeToElement(new
                {
                    batchId,
                    commandId = cmd.CommandId,
                    turnId = cmd.TurnId,
                    conversationId,
                    userMessageId = request.ClientMessageId,
                    clientRequestId = request.ClientRequestId,
                    agentId = cmd.AgentInstanceId,
                    attachments,
                    metadata = request.Metadata?.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                }, JsonOpts);

                events.Add(new ConversationEventEntity
                {
                    ConversationId = conversationId,
                    Sequence = seq,
                    EventId = Guid.NewGuid().ToString("N"),
                    WorkspaceId = workspaceId,
                    TurnId = cmd.TurnId,
                    CommandId = cmd.CommandId,
                    RunId = null,
                    MessageId = request.ClientMessageId,
                    Type = ConversationEventTypes.TurnAccepted,
                    SchemaVersion = 1,
                    Payload = payload.GetRawText(),
                    OccurredAt = nowUtc.ToString("O"),
                    CommittedAt = nowUtc.ToString("O"),
                    CorrelationId = conversationId,
                    TraceId = cmd.TraceId,
                    AgentId = goalFence is null ? null : cmd.AgentInstanceId,
                    SourceKind = goalFence is null
                        ? ConversationEventSourceKind.User.ToString().ToLowerInvariant()
                        : ConversationEventSourceKind.Goal.ToString().ToLowerInvariant(),
                    ProducerComponent = goalFence is null
                        ? "chat.acceptance"
                        : GoalProducerComponents.Continuation,
                });
            }

            if (goalFence is not null)
            {
                var (goal, outbox) = goalFence.Value;
                var cmd = commands.Single();
                var acceptedSequence = headSeq + commands.Count + 1;

                goal.IterationsStarted++;
                goal.AggregateVersion++;
                goal.UpdatedAtUtc = nowUtc;

                db.GoalIterations.Add(new GoalIterationEntity
                {
                    GoalIterationId = $"gi-{goal.GoalRunId}-{goalContinuation!.ActivationEpoch}-{goalContinuation.IterationNo}",
                    GoalRunId = goal.GoalRunId,
                    ActivationEpoch = goalContinuation.ActivationEpoch,
                    IterationNo = goalContinuation.IterationNo,
                    Status = "accepted",
                    CommandId = cmd.CommandId,
                    TurnId = cmd.TurnId,
                    TraceId = cmd.TraceId,
                    AcceptedSequence = acceptedSequence,
                    CreatedAtUtc = nowUtc,
                });

                outbox.Status = GoalOutboxValues.Completed;
                outbox.LeaseOwner = null;
                outbox.LeaseUntilUtc = null;
                outbox.LastError = null;
                outbox.CompletedAtUtc = nowUtc;

                events.Add(BuildGoalContinuationEvent(
                    goal,
                    outbox,
                    cmd,
                    request.ClientMessageId,
                    acceptedSequence,
                    GoalEventTypes.IterationAccepted,
                    new
                    {
                        goalRunId = goal.GoalRunId,
                        activationEpoch = goalContinuation.ActivationEpoch,
                        aggregateVersion = goal.AggregateVersion,
                        iterationNumber = goalContinuation.IterationNo,
                        commandId = cmd.CommandId,
                        turnId = cmd.TurnId,
                        acceptedSequence,
                    }));
                events.Add(BuildGoalContinuationEvent(
                    goal,
                    outbox,
                    cmd,
                    request.ClientMessageId,
                    acceptedSequence + 1,
                    GoalEventTypes.ContinuationDispatched,
                    new
                    {
                        goalRunId = goal.GoalRunId,
                        activationEpoch = goalContinuation.ActivationEpoch,
                        aggregateVersion = goal.AggregateVersion,
                        iterationNumber = goalContinuation.IterationNo,
                        outboxId = outbox.OutboxId,
                        commandId = cmd.CommandId,
                        turnId = cmd.TurnId,
                    }));
            }
            db.ConversationEvents.AddRange(events);

            // 2d.5: Create ConversationTurn for each command
            foreach (var cmd in commands)
            {
                db.ConversationTurns.Add(new ConversationTurnEntity
                {
                    ConversationId = conversationId,
                    TurnId = cmd.TurnId,
                    CommandId = cmd.CommandId,
                    WorkspaceId = workspaceId,
                    UserId = userId,
                    Status = "accepted",
                    AcceptedSequence = headSeq + commands.IndexOf(cmd) + 1,
                    CreatedAt = now,
                });
            }

            // 2e: 更新 Conversation Head
            var head = await db.ConversationHeads
                .FirstOrDefaultAsync(h => h.ConversationId == conversationId, ct);
            if (head is null)
            {
                head = new ConversationHeadEntity
                {
                    ConversationId = conversationId,
                    HeadSequence = headSeq + eventCount,
                };
                db.ConversationHeads.Add(head);
            }
            else
            {
                head.HeadSequence = headSeq + eventCount;
                db.ConversationHeads.Update(head);
            }

            // 2f: 记录 acceptedSequence
            batch.AcceptedSequence = headSeq + eventCount;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            committedSignal.Signal(conversationId, batch.AcceptedSequence);

            logger.LogInformation(
                "[AcceptStore] Committed batch={BatchId} conv={ConvId} cmds={Count} seq=[{First},{Last}]",
                batchId, conversationId, commands.Count,
                headSeq + 1, batch.AcceptedSequence);

            return new AcceptanceResult
            {
                ConversationId = conversationId,
                MessageId = request.ClientMessageId,
                TurnIds = commands.Select(c => c.TurnId).ToList(),
                CommandIds = commands.Select(c => c.CommandId).ToList(),
                AcceptedSequence = batch.AcceptedSequence,
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// 原子分配 N 个 sequence 号（单行 UPDATE conversation_heads SET head_sequence = head_sequence + N）。
    /// </summary>
    private async Task<long> AllocateSequencesAsync(
        string conversationId, int count, CancellationToken ct)
    {
        // Ensure head row exists
        var head = await db.ConversationHeads
            .FirstOrDefaultAsync(h => h.ConversationId == conversationId, ct);

        if (head is null)
        {
            head = new ConversationHeadEntity
            {
                ConversationId = conversationId,
                HeadSequence = 0,
            };
            db.ConversationHeads.Add(head);
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var prev = head.HeadSequence;
        head.HeadSequence += count;
        db.ConversationHeads.Update(head);
        await db.SaveChangesAsync(ct);

        return prev;
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        try
        {
            return JsonSerializer.Serialize(metadata, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetMetadataValue(
        IReadOnlyDictionary<string, string>? metadata,
        string key)
        => metadata is not null
           && metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private async Task<(GoalRunEntity Goal, GoalOutboxEntity Outbox)> ValidateGoalContinuationAsync(
        GoalContinuationAcceptanceContext context,
        string workspaceId,
        string conversationId,
        IReadOnlyList<string> agentIds,
        CancellationToken ct)
    {
        if (agentIds.Count != 1)
            throw Reject(GoalContinuationAcceptanceErrorCodes.IterationConflict,
                "Goal continuation must target exactly one Agent.");

        var goal = await db.GoalRuns
            .SingleOrDefaultAsync(item => item.GoalRunId == context.GoalRunId, ct)
            ?? throw Reject(GoalContinuationAcceptanceErrorCodes.GoalMissing,
                $"Goal {context.GoalRunId} no longer exists.");

        if (!string.Equals(goal.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || !string.Equals(goal.CurrentConversationId, conversationId, StringComparison.Ordinal)
            || !string.Equals(goal.AgentInstanceId, agentIds[0], StringComparison.Ordinal))
        {
            throw Reject(GoalContinuationAcceptanceErrorCodes.StaleEpoch,
                "Goal continuation routing no longer matches the Goal aggregate.");
        }
        if (goal.Status != GoalPhase.Active)
            throw Reject(GoalContinuationAcceptanceErrorCodes.GoalInactive,
                $"Goal is {goal.Status}, not active.");
        if (goal.ActivationEpoch != context.ActivationEpoch)
            throw Reject(GoalContinuationAcceptanceErrorCodes.StaleEpoch,
                $"Goal epoch changed from {context.ActivationEpoch} to {goal.ActivationEpoch}.");
        if (goal.AggregateVersion != context.AggregateVersion)
            throw Reject(GoalContinuationAcceptanceErrorCodes.StaleVersion,
                $"Goal version changed from {context.AggregateVersion} to {goal.AggregateVersion}.");
        if (!GoalStateMachine.CanAcceptNewIteration(
                goal.Status,
                goal.MaxIterations,
                goal.IterationsStarted))
        {
            throw Reject(GoalContinuationAcceptanceErrorCodes.BudgetExhausted,
                "Goal has no remaining accepted Iteration budget.");
        }
        if (context.IterationNo != goal.IterationsStarted + 1
            || goal.IterationsSettled != goal.IterationsStarted)
        {
            throw Reject(GoalContinuationAcceptanceErrorCodes.IterationConflict,
                "Another Goal Iteration is already accepted or the requested number is stale.");
        }

        var outbox = await db.GoalOutbox.SingleOrDefaultAsync(
            item => item.OutboxId == context.OutboxId, ct);
        if (outbox is null
            || outbox.Status != GoalOutboxValues.Leased
            || !string.Equals(outbox.GoalRunId, context.GoalRunId, StringComparison.Ordinal)
            || outbox.ActivationEpoch != context.ActivationEpoch
            || outbox.AggregateVersion != context.AggregateVersion
            || !string.Equals(outbox.LeaseOwner, context.LeaseOwner, StringComparison.Ordinal)
            || outbox.FencingToken != context.FencingToken
            || outbox.LeaseUntilUtc is null
            || outbox.LeaseUntilUtc <= _timeProvider.GetUtcNow())
        {
            throw Reject(GoalContinuationAcceptanceErrorCodes.StaleLease,
                "Goal continuation outbox lease is missing, expired or fenced out.");
        }

        var binding = await db.TaskGoalBindings.SingleOrDefaultAsync(
            item => item.GoalRunId == goal.GoalRunId, ct);
        if (binding is not null)
        {
            var task = await db.WorkspaceTasks.SingleOrDefaultAsync(
                item => item.WorkspaceId == binding.WorkspaceId
                    && item.TaskId == binding.TaskId, ct);
            var reservation = binding.ReservationId is null
                ? null
                : await db.AgentExecutionReservations.SingleOrDefaultAsync(
                    item => item.ReservationId == binding.ReservationId, ct);
            if (binding.Status != "active"
                || context.TaskId != binding.TaskId
                || context.ExpectedTaskVersion != binding.ExpectedTaskVersion
                || context.ReservationFencingToken != binding.ReservationFencingToken
                || task is null
                || task.Version != binding.ExpectedTaskVersion
                || task.ActiveAssignmentId != binding.AssignmentId
                || task.Status is not (PuddingCode.Tasks.WorkspaceTaskStatus.Assigned
                    or PuddingCode.Tasks.WorkspaceTaskStatus.InProgress)
                || reservation is null
                || reservation.Status != "active"
                || reservation.FencingToken != binding.ReservationFencingToken
                || reservation.LeaseUntilUtc <= _timeProvider.GetUtcNow()
                || reservation.TaskId != binding.TaskId
                || reservation.AgentId != binding.AgentInstanceId
                || reservation.GoalRunId != binding.GoalRunId)
            {
                throw Reject(
                    GoalContinuationAcceptanceErrorCodes.TaskFenceChanged,
                    "Task-bound Goal ownership, Task version or reservation fence changed before acceptance.");
            }

            TaskNodeEntity? workUnit = null;
            if (!string.IsNullOrWhiteSpace(binding.TaskPlanId))
            {
                var plan = await db.TaskPlanRuns.SingleOrDefaultAsync(
                    item => item.PlanId == binding.TaskPlanId, ct);
                var workUnits = await db.TaskNodes
                    .Where(item => item.PlanId == binding.TaskPlanId && item.Depth == 1)
                    .OrderBy(item => item.SequenceNo)
                    .ToListAsync(ct);
                workUnit = workUnits.FirstOrDefault(item =>
                    item.Status is not ("Completed" or "Cancelled" or "Superseded"));
                var predecessorsCompleted = workUnit is not null
                    && workUnits.Where(item => item.SequenceNo < workUnit.SequenceNo)
                        .All(item => item.Status == TaskNodeStatuses.Completed.ToString());

                if (plan is null
                    || plan.Status != TaskPlanStatuses.Active.ToString()
                    || plan.WorkspaceId != binding.WorkspaceId
                    || plan.WorkspaceTaskId != binding.TaskId
                    // WorkspaceTaskVersion is the immutable compile-time input embedded in
                    // PlanFingerprint. Live Task revisions are fenced by the binding above.
                    || plan.LeaderAgentId != binding.AgentInstanceId
                    || string.IsNullOrWhiteSpace(binding.PlanFingerprint)
                    || plan.PlanFingerprint != binding.PlanFingerprint
                    || context.TaskPlanId != binding.TaskPlanId
                    || context.TaskPlanFingerprint != binding.PlanFingerprint
                    || workUnit is null
                    || context.TaskNodeId != workUnit.TaskNodeId
                    || context.ParentTaskNodeId != workUnit.ParentTaskNodeId
                    || workUnit.AssignedToId != binding.AgentInstanceId
                    || workUnit.Status is not ("Draft" or "Planned" or "Assigned" or "Running")
                    || !predecessorsCompleted
                    || string.IsNullOrWhiteSpace(workUnit.WorkUnitKind)
                    || string.IsNullOrWhiteSpace(workUnit.Objective)
                    || workUnit.MaxRounds is null or <= 0
                    || workUnit.MaxToolCalls is null or <= 0
                    || workUnit.MaxDurationSeconds is null or <= 0
                    || workUnit.MaxInputTokens is null or <= 0
                    || workUnit.MaxOutputTokens is null or <= 0
                    || workUnit.MaxCost is null or <= 0)
                {
                    throw Reject(
                        GoalContinuationAcceptanceErrorCodes.TaskPlanChanged,
                        "Task execution plan or current WorkUnit changed before acceptance.");
                }
            }
            else if (context.TaskPlanId is not null
                     || context.TaskPlanFingerprint is not null
                     || context.TaskNodeId is not null
                     || context.ParentTaskNodeId is not null)
            {
                throw Reject(
                    GoalContinuationAcceptanceErrorCodes.TaskPlanChanged,
                    "Goal binding has no execution plan but the continuation supplied one.");
            }

            var renewedAtUtc = _timeProvider.GetUtcNow();
            reservation.LeaseUntilUtc = renewedAtUtc.Add(_taskBoundReservationLease);
            reservation.UpdatedAtUtc = renewedAtUtc;
            if (workUnit is not null)
            {
                workUnit.Status = TaskNodeStatuses.Running.ToString();
                workUnit.StartedAt ??= renewedAtUtc.ToUnixTimeMilliseconds();
                workUnit.UpdatedAt = renewedAtUtc.ToUnixTimeMilliseconds();
            }
        }

        var conversationBusy = await db.ChatExecutionCommands.AnyAsync(
            command => command.SessionId == conversationId
                && (command.Status == "pending"
                    || command.Status == "leased"
                    || command.Status == "running"
                    || command.Status == "cancel_requested"), ct);
        if (conversationBusy)
        {
            throw new GoalContinuationAcceptanceException(
                GoalContinuationAcceptanceErrorCodes.ConversationBusy,
                "Conversation has an earlier accepted or running Turn.",
                deferred: true);
        }

        return (goal, outbox);
    }

    private static GoalContinuationAcceptanceException Reject(string code, string message)
        => new(code, message);

    private ConversationEventEntity BuildGoalContinuationEvent(
        GoalRunEntity goal,
        GoalOutboxEntity outbox,
        ChatExecutionCommandEntity command,
        string messageId,
        long sequence,
        string eventType,
        object payload)
        => new()
        {
            ConversationId = goal.CurrentConversationId,
            Sequence = sequence,
            EventId = $"{(eventType == GoalEventTypes.IterationAccepted ? "gia" : "gcd")}-{outbox.OutboxId}",
            WorkspaceId = goal.WorkspaceId,
            TurnId = command.TurnId,
            CommandId = command.CommandId,
            RunId = null,
            MessageId = messageId,
            Type = eventType,
            SchemaVersion = 1,
            Payload = JsonSerializer.SerializeToElement(payload, JsonOpts).GetRawText(),
            OccurredAt = _timeProvider.GetUtcNow().ToString("O"),
            CommittedAt = _timeProvider.GetUtcNow().ToString("O"),
            CorrelationId = goal.GoalRunId,
            CausationId = outbox.OutboxId,
            AgentId = goal.AgentInstanceId,
            SourceKind = ConversationEventSourceKind.Goal.ToString().ToLowerInvariant(),
            TraceId = command.TraceId,
            ProducerComponent = GoalProducerComponents.Continuation,
        };
}
