using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-074 §12.5: Goal 聚合写入原语 —— 状态变更与对应 ConversationEvent 在同一
/// SQLite 事务提交（照抄 ConversationAcceptanceStore 的直写实体 + head 序列分配模式，
/// 不能走自开事务的 IConversationEventStore.AppendAsync）。
/// Scoped 服务；所有写方法在 Serializable 事务内重读行做乐观并发裁决。
/// </summary>
public sealed class GoalRunStore(
    PlatformDbContext db,
    ICommittedEventSignal committedSignal,
    ILogger<GoalRunStore> logger,
    GoalOutboxSignal? outboxSignal = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>与 Goal 命令事务一起提交的 canonical 事件内容。</summary>
    public sealed record GoalEventAppend(
        string EventType,
        object Payload,
        string? CausationId = null,
        string ProducerComponent = GoalProducerComponents.Command);

    public async Task<GoalRunEntity?> FindAsync(string goalRunId, CancellationToken ct = default)
        => await db.GoalRuns.AsNoTracking()
            .FirstOrDefaultAsync(g => g.GoalRunId == goalRunId, ct);

    public async Task<GoalRunEntity?> FindActiveAsync(
        string conversationId, string agentInstanceId, CancellationToken ct = default)
        // partial unique 索引保证 (conversation, agent) 至多一个非终态 Goal，无需排序。
        => await db.GoalRuns.AsNoTracking()
            .FirstOrDefaultAsync(g => g.CurrentConversationId == conversationId
                && g.AgentInstanceId == agentInstanceId
                && (g.Status == GoalPhase.Active
                    || g.Status == GoalPhase.Paused
                    || g.Status == GoalPhase.Blocked), ct);

    public async Task<GoalRunEntity?> FindLatestAsync(
        string workspaceId, string conversationId, CancellationToken ct = default)
    {
        // EF SQLite 不翻译 DateTimeOffset ORDER BY；取回后内存排序（每会话 Goal 数量有界）。
        var candidates = await db.GoalRuns.AsNoTracking()
            .Where(g => g.WorkspaceId == workspaceId
                && g.CurrentConversationId == conversationId)
            .ToListAsync(ct);
        return candidates
            .OrderByDescending(g => g.CreatedAtUtc)
            .FirstOrDefault();
    }

    public async Task<GoalRunEntity?> FindBySourceCommandAsync(
        string sourceCommandId, CancellationToken ct = default)
        => await db.GoalRuns.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SourceCommandId == sourceCommandId, ct);

    public async Task<TaskGoalBindingEntity?> FindTaskBindingAsync(
        string goalRunId, CancellationToken ct = default)
        => await db.TaskGoalBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GoalRunId == goalRunId, ct);

    public async Task<WorkspaceTaskEntity?> FindTaskAsync(
        string workspaceId, string taskId, CancellationToken ct = default)
        => await db.WorkspaceTasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId
                && item.TaskId == taskId, ct);

    /// <summary>
    /// Returns the first non-terminal leaf in the frozen Task execution plan.
    /// Sequence order is canonical; a later WorkUnit cannot overtake an earlier one.
    /// </summary>
    public async Task<TaskNodeEntity?> FindCurrentTaskWorkUnitAsync(
        string taskPlanId, CancellationToken ct = default)
        => await db.TaskNodes.AsNoTracking()
            .Where(item => item.PlanId == taskPlanId
                && item.Depth == 1
                && item.Status != "Completed"
                && item.Status != "Cancelled"
                && item.Status != "Superseded")
            .OrderBy(item => item.SequenceNo)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GoalIterationEntity>> GetIterationsAsync(
        string goalRunId, CancellationToken ct = default)
        => await db.GoalIterations.AsNoTracking()
            .Where(i => i.GoalRunId == goalRunId)
            .OrderBy(i => i.IterationNo)
            .ToListAsync(ct);

    /// <summary>
    /// 创建 Goal（提交即 active）并同事务写 goal.created + goal.activated。
    /// source_command_id 唯一索引保证创建幂等；冲突抛 GoalVersionConflictException。
    /// </summary>
    public async Task<GoalRunEntity> CreateAsync(
        GoalRunEntity goal,
        string traceId,
        CancellationToken ct = default,
        bool enqueueContinuation = false)
    {
        var continuationEnqueued = false;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            goal.CreatedAtUtc = DateTimeOffset.UtcNow;
            goal.UpdatedAtUtc = goal.CreatedAtUtc;
            db.GoalRuns.Add(goal);
            await db.SaveChangesAsync(ct);

            var events = new List<GoalEventAppend>
            {
                new(GoalEventTypes.Created, BuildPayload(goal)),
                new(GoalEventTypes.Activated, BuildPayload(goal)),
            };
            if (enqueueContinuation && TryEnqueueContinuation(goal, goal.CreatedAtUtc, out _))
            {
                continuationEnqueued = true;
                events.Add(new GoalEventAppend(
                    GoalEventTypes.ContinuationRequested,
                    BuildContinuationPayload(goal),
                    ProducerComponent: GoalProducerComponents.Continuation));
            }

            await AppendEventsAsync(goal, traceId, events, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        committedSignal.Signal(goal.CurrentConversationId, -1);
        if (continuationEnqueued)
            outboxSignal?.Signal();

        logger.LogInformation(
            "[GoalStore] Created goal={GoalRunId} conv={ConversationId} agent={AgentInstanceId} maxIterations={MaxIterations}",
            goal.GoalRunId, goal.CurrentConversationId, goal.AgentInstanceId, goal.MaxIterations);

        return goal;
    }

    /// <summary>
    /// CAS 状态迁移：expectedVersion &lt;= 0 表示"取最新"。
    /// 迁移委托在事务内对 tracked 实体执行并返回是否应用 —— 返回 false 时整个事务
    /// 不提交（不递增 version、不写事件），用于状态卫兵失败的场景。
    /// 本方法统一递增 aggregate_version 并在成功后同事务写一个 goal.* 事件。
    /// </summary>
    public async Task<(GoalRunEntity? Goal, bool VersionConflict)> TryMutateAsync(
        string goalRunId,
        int expectedVersion,
        Func<GoalRunEntity, bool> mutate,
        GoalEventAppend append,
        string traceId,
        CancellationToken ct = default,
        bool enqueueContinuation = false)
    {
        GoalRunEntity? goal = null;
        var continuationEnqueued = false;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            goal = await db.GoalRuns
                .FirstOrDefaultAsync(g => g.GoalRunId == goalRunId, ct);
            if (goal is null)
                return (null, false);

            if (expectedVersion > 0 && goal.AggregateVersion != expectedVersion)
            {
                logger.LogWarning(
                    "[GoalStore] CAS conflict goal={GoalRunId} expected={Expected} actual={Actual}",
                    goalRunId, expectedVersion, goal.AggregateVersion);
                return (null, true);
            }

            if (!mutate(goal))
                return (null, false);

            goal.AggregateVersion++;
            goal.UpdatedAtUtc = DateTimeOffset.UtcNow;

            var events = new List<GoalEventAppend> { append };
            if (enqueueContinuation && TryEnqueueContinuation(goal, goal.UpdatedAtUtc, out _))
            {
                continuationEnqueued = true;
                events.Add(new GoalEventAppend(
                    GoalEventTypes.ContinuationRequested,
                    BuildContinuationPayload(goal),
                    ProducerComponent: GoalProducerComponents.Continuation));
            }

            await AppendEventsAsync(goal, traceId, events, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        committedSignal.Signal(goal!.CurrentConversationId, -1);
        if (continuationEnqueued)
            outboxSignal?.Signal();
        return (goal, false);
    }

    private bool TryEnqueueContinuation(
        GoalRunEntity goal,
        DateTimeOffset dueAtUtc,
        out GoalOutboxEntity? outbox)
    {
        outbox = null;
        if (!GoalStateMachine.CanAcceptNewIteration(
                goal.Status,
                goal.MaxIterations,
                goal.IterationsStarted)
            || goal.IterationsSettled != goal.IterationsStarted)
            return false;

        var iterationNo = goal.IterationsStarted + 1;
        var outboxId = $"gc-{goal.GoalRunId}-{goal.ActivationEpoch}-{iterationNo}";
        outbox = new GoalOutboxEntity
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
                iterationNo,
            }, JsonOpts),
            Status = GoalOutboxValues.Pending,
            DueAtUtc = dueAtUtc,
            FencingToken = 0,
            AttemptCount = 0,
            CreatedAtUtc = dueAtUtc,
        };
        db.GoalOutbox.Add(outbox);
        return true;
    }

    /// <summary>同事务分配 sequence 并直写 ConversationEventEntity（envelope 规则见 ADR-074 §12.5）。</summary>
    private async Task AppendEventsAsync(
        GoalRunEntity goal,
        string traceId,
        IReadOnlyList<GoalEventAppend> events,
        CancellationToken ct)
    {
        var headSeq = await AllocateSequencesAsync(goal.CurrentConversationId, events.Count, ct);
        for (var i = 0; i < events.Count; i++)
        {
            var append = events[i];
            // eventId 确定性：类型 + goalRunId + 目标 aggregate_version（created 用 1）。
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = headSeq + i + 1,
                EventId = $"{append.EventType}:{goal.GoalRunId}:{goal.AggregateVersion}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = string.Empty,
                CommandId = goal.SourceCommandId,
                RunId = null,
                MessageId = null,
                Type = append.EventType,
                SchemaVersion = 1,
                Payload = JsonSerializer.SerializeToElement(append.Payload, JsonOpts).GetRawText(),
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = goal.GoalRunId,
                CausationId = append.CausationId,
                ProducerEventId = null,
                AgentId = goal.AgentInstanceId,
                SourceKind = ConversationEventSourceKind.Goal.ToString().ToLowerInvariant(),
                TraceId = traceId,
                ProducerComponent = append.ProducerComponent,
            });
        }

        var head = await db.ConversationHeads
            .FirstOrDefaultAsync(h => h.ConversationId == goal.CurrentConversationId, ct);
        if (head is null)
        {
            db.ConversationHeads.Add(new ConversationHeadEntity
            {
                ConversationId = goal.CurrentConversationId,
                HeadSequence = headSeq + events.Count,
            });
        }
        else
        {
            head.HeadSequence = headSeq + events.Count;
            db.ConversationHeads.Update(head);
        }
    }

    /// <summary>原子分配 N 个 sequence（与 ConversationAcceptanceStore 相同的单行递增语义）。</summary>
    private async Task<long> AllocateSequencesAsync(
        string conversationId, int count, CancellationToken ct)
    {
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

    private static object BuildPayload(GoalRunEntity goal) => new
    {
        goalRunId = goal.GoalRunId,
        workspaceId = goal.WorkspaceId,
        conversationId = goal.CurrentConversationId,
        agentInstanceId = goal.AgentInstanceId,
        status = goal.Status.ToString().ToLowerInvariant(),
        objective = goal.Objective,
        objectiveVersion = goal.ObjectiveVersion,
        maxIterations = goal.MaxIterations,
        iterationsStarted = goal.IterationsStarted,
        iterationsSettled = goal.IterationsSettled,
        activationEpoch = goal.ActivationEpoch,
        aggregateVersion = goal.AggregateVersion,
        reason = goal.StatusReason,
        blockedCode = goal.BlockedCode,
        sourceChannel = goal.SourceChannel,
    };

    private static object BuildContinuationPayload(GoalRunEntity goal) => new
    {
        goalRunId = goal.GoalRunId,
        activationEpoch = goal.ActivationEpoch,
        aggregateVersion = goal.AggregateVersion,
        iterationNumber = goal.IterationsStarted + 1,
        remainingIterations = goal.MaxIterations - goal.IterationsStarted,
    };
}
