using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Tasks;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 账本尾游标桥：轮询 task_events / conversation_events 的新行（id &gt; 游标），按事件清单
/// 过滤后入队 task_scheduler_intents，供 <see cref="TaskSchedulingCoordinator"/> 即时评估。
/// <para>
/// 游标恢复点 = MAX(source_event_id)（<see cref="ITaskSchedulerIntentStore.GetTailCursorAsync"/>），
/// 启动时从当前账本 MAX 起步，不回放历史。shadow 模式下跳过入队、只推进游标（Coordinator
/// 不存在，入队会积压；候选决策由 Worker 5m 扫描兜底）。completed/failed 事件入队用于触发
/// availability 相关的恢复评估。
/// </para>
/// </summary>
public sealed class TaskEventLedgerTailBridge(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ITaskSchedulerIntentStore intentStore,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<TaskEventLedgerTailBridge> logger) : BackgroundService
{
    /// <summary>触发调度评估的 task_events 事件清单。</summary>
    private static readonly IReadOnlySet<TaskEventType> TaskEventFilter =
        new HashSet<TaskEventType>
        {
            TaskEventType.TaskReady,
            TaskEventType.TaskUpdated,
            TaskEventType.TaskDeferred,
            TaskEventType.TaskBlocked,
            TaskEventType.TaskReopened,
            TaskEventType.TaskCompleted,
            TaskEventType.TaskFailed,
        };

    /// <summary>触发调度评估的 conversation_events 终态清单（goal terminal 及 task-goal 变体，ADR-074 目录）。</summary>
    private static readonly IReadOnlySet<string> ConversationEventFilter =
        new HashSet<string>(StringComparer.Ordinal)
        {
            GoalEventTypes.Completed,        // goal.completed
            GoalEventTypes.Failed,           // goal.failed
            GoalEventTypes.BudgetExhausted,  // goal.budget_exhausted
            GoalEventTypes.Cancelled,        // goal.cancelled
            GoalEventTypes.TaskGoalCompleted, // task.goal.completed
            GoalEventTypes.TaskGoalBlocked,   // task.goal.blocked
        };

    // -1 = 未初始化；首次 Poll 时以当前账本 MAX(id) 为起点（不回放历史，缺口由 Worker 5m 扫描兑底）。
    private long _taskEventsCursor = -1;
    private long _conversationEventsCursor = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            try
            {
                if (current.Enabled && current.EventDrivenEnabled)
                    await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[TaskEventLedgerTailBridge] poll failed; retry next interval");
            }

            try
            {
                await Task.Delay(
                    current.IntentPollInterval <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(2)
                        : current.IntentPollInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>游标初始化：以当前账本 MAX(id) 为起点（幂等；ExecuteAsync 与测试均先调用）。</summary>
    public async Task InitializeCursorsAsync(CancellationToken ct = default)
    {
        if (_taskEventsCursor < 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            _taskEventsCursor = await db.TaskEvents.MaxAsync(item => (long?)item.Id, ct) ?? 0;
            _conversationEventsCursor = await db.ConversationEvents.MaxAsync(item => (long?)item.Id, ct) ?? 0;
            logger.LogInformation(
                "[TaskEventLedgerTailBridge] cursors initialized taskCursor={TaskCursor} conversationCursor={ConversationCursor}",
                _taskEventsCursor,
                _conversationEventsCursor);
        }
    }

    /// <summary>执行一轮账本尾扫描（测试入口；ExecuteAsync 循环调用同一方法）。</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        await InitializeCursorsAsync(ct);
        var current = options.CurrentValue;
        var batch = Math.Clamp(current.IntentBatchSize, 1, 500);
        // shadow 模式跳过入队、只推进游标（Coordinator 不存在，入队只会在表里积压）。
        var enqueue = current.Enabled
            && current.EventDrivenEnabled
            && string.Equals(current.Mode, "authoritative", StringComparison.OrdinalIgnoreCase);
        _taskEventsCursor = await PollTaskEventsAsync(_taskEventsCursor, batch, enqueue, current, ct);
        _conversationEventsCursor = await PollConversationEventsAsync(
            _conversationEventsCursor, batch, enqueue, current, ct);
    }

    private async Task<long> PollTaskEventsAsync(
        long cursor,
        int batch,
        bool enqueue,
        TaskAutoDispatchOptions current,
        CancellationToken ct)
    {
        long newest = cursor;
        int enqueued = 0, skipped = 0;
        List<Data.Entities.TaskEventEntity> rows;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.TaskEvents
                .AsNoTracking()
                .Where(item => item.Id > cursor)
                .OrderBy(item => item.Id)
                .Take(batch)
                .ToListAsync(ct);
        }

        foreach (var row in rows)
        {
            newest = Math.Max(newest, row.Id);
            if (!enqueue
                || !TaskEventFilter.Contains(row.EventType)
                || !IsWatchedWorkspace(current, row.WorkspaceId))
            {
                skipped++;
                continue;
            }

            var written = await intentStore.EnqueueAsync(new TaskSchedulerIntentEnvelope
            {
                WorkspaceId = row.WorkspaceId,
                Source = TaskSchedulerIntentSources.TaskEvents,
                SourceEventId = row.Id,
                EventType = ToWireType(row.EventType),
                TaskId = row.TaskId,
                GoalRunId = null,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    type = ToWireType(row.EventType),
                    event_id = row.EventId,
                    priority = row.Priority,
                    decision_code = row.DecisionCode,
                }),
                CreatedAtUtc = timeProvider.GetUtcNow(),
            }, ct);
            if (written)
                enqueued++;
        }

        if (rows.Count > 0)
        {
            logger.LogDebug(
                "[TaskEventLedgerTailBridge] task_events scanned={Scanned} enqueued={Enqueued} skipped={Skipped} cursor={Cursor}",
                rows.Count, enqueued, skipped, newest);
        }

        return newest;
    }

    private async Task<long> PollConversationEventsAsync(
        long cursor,
        int batch,
        bool enqueue,
        TaskAutoDispatchOptions current,
        CancellationToken ct)
    {
        long newest = cursor;
        int enqueued = 0, skipped = 0;
        List<Data.Entities.ConversationEventEntity> rows;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.ConversationEvents
                .AsNoTracking()
                .Where(item => item.Id > cursor)
                .OrderBy(item => item.Id)
                .Take(batch)
                .ToListAsync(ct);
        }

        foreach (var row in rows)
        {
            newest = Math.Max(newest, row.Id);
            if (!enqueue
                || !ConversationEventFilter.Contains(row.Type)
                || !IsWatchedWorkspace(current, row.WorkspaceId))
            {
                skipped++;
                continue;
            }

            var written = await intentStore.EnqueueAsync(new TaskSchedulerIntentEnvelope
            {
                WorkspaceId = row.WorkspaceId,
                Source = TaskSchedulerIntentSources.ConversationEvents,
                SourceEventId = row.Id,
                EventType = row.Type,
                TaskId = null,
                GoalRunId = string.IsNullOrWhiteSpace(row.RunId) ? null : row.RunId,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    type = row.Type,
                    conversation_id = row.ConversationId,
                    correlation_id = row.CorrelationId,
                }),
                CreatedAtUtc = timeProvider.GetUtcNow(),
            }, ct);
            if (written)
                enqueued++;
        }

        if (rows.Count > 0)
        {
            logger.LogDebug(
                "[TaskEventLedgerTailBridge] conversation_events scanned={Scanned} enqueued={Enqueued} skipped={Skipped} cursor={Cursor}",
                rows.Count, enqueued, skipped, newest);
        }

        return newest;
    }

    private static bool IsWatchedWorkspace(TaskAutoDispatchOptions current, string workspaceId) =>
        (current.WorkspaceIds ?? []).Contains(workspaceId, StringComparer.Ordinal)
        && !(current.PausedWorkspaceIds ?? []).Contains(workspaceId, StringComparer.Ordinal);

    private static string ToWireType(TaskEventType eventType) => eventType switch
    {
        TaskEventType.TaskCreated => "task.created",
        TaskEventType.TaskUpdated => "task.updated",
        TaskEventType.TaskReady => "task.ready",
        TaskEventType.TaskDeferred => "task.deferred",
        TaskEventType.TaskReserved => "task.reserved",
        TaskEventType.TaskAssigned => "task.assigned",
        TaskEventType.TaskAccepted => "task.accepted",
        TaskEventType.TaskProgressed => "task.progressed",
        TaskEventType.TaskBlocked => "task.blocked",
        TaskEventType.TaskAssignmentRejected => "task.assignment_rejected",
        TaskEventType.TaskCompleted => "task.completed",
        TaskEventType.TaskFailed => "task.failed",
        TaskEventType.TaskReopened => "task.reopened",
        TaskEventType.TaskCancelled => "task.cancelled",
        TaskEventType.TaskArchived => "task.archived",
        TaskEventType.TaskDispatchRequested => "task.dispatch.requested",
        TaskEventType.TaskDispatchDeferred => "task.dispatch.deferred",
        TaskEventType.TaskEvaluated => "task.evaluated",
        _ => eventType.ToString(),
    };
}
