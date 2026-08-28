using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-03: WorkspaceTask Command 服务 — 封装「状态机校验 + Store CAS + Assignment 记录 + AppendEvent」原子语义。
/// <para>
/// 命令端点（Assign/RunNow/Cancel/Reopen/Archive/MarkFailed/Resume/Requeue）需要把状态迁移、
/// 版本递增、事件追加、Assignment 记录/释放放进同一提交单元。TB-01 冻结的 <see cref="ITaskStore"/>
/// 不含状态迁移方法（UpdateTaskRequest 无法改 status / active_assignment_id），故本服务对命令转换
/// 直接通过 <see cref="IDbContextFactory{TContext}"/> 操作 EF 模型，一次 SaveChanges 原子提交，
/// CAS 用「读当前 version → 比对 → 更新 version+1」实现（单写者语义，与 SqliteWorkspaceTaskStore 的 CAS 一致）。
/// </para>
/// </summary>
public sealed class TaskCommandService(
    ITaskStore store,
    IDbContextFactory<PlatformDbContext> dbFactory)
{
    private readonly ITaskStore _store = store;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory = dbFactory;

    /// <summary>PATCH：状态机校验（非终态保持当前状态）+ Store CAS 更新（task.updated 事件）。
    /// <para>B1：可选 <paramref name="status"/> 非空且 != 当前状态时，经 <see cref="TaskStateMachine.CanTransition"/> 校验后原子迁移状态 + 字段更新 + 事件。</para>
    /// </summary>
    public async Task<WorkspaceTask> PatchAsync(
        string workspaceId,
        string taskId,
        int expectedVersion,
        string? title,
        string? description,
        string? acceptanceCriteria,
        TaskPriority? priority,
        TaskExecutionWindow? executionWindow,
        string? preferredAgentId,
        DateTimeOffset? notBeforeUtc,
        DateTimeOffset? dueAtUtc,
        long? sortOrder,
        WorkspaceTaskStatus? status = null,
        string? updatedBy = null,
        CancellationToken ct = default,
        string? taskType = null,
        IReadOnlyList<string>? requiredCapabilityIds = null,
        string? requiredProviderId = null,
        string? requiredModelId = null,
        bool? allowAgentFallback = null,
        bool? autoDispatchEnabled = null)
    {
        var current = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (current is null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId,
                expectedVersion,
                null);
        }

        // 无状态迁移（status 缺省或 target == current）：纯字段更新（现状不变，task.updated）。
        if (status is null || status.Value == current.Status)
        {
            if (!TaskStateMachine.TryApplyCommand(current.Status, TaskCommand.Update, out _))
            {
                throw new TaskStoreException(
                    TaskErrorCode.TaskInvalidTransition,
                    $"Task '{taskId}' in terminal status '{current.Status}' cannot be updated.",
                    taskId,
                    expectedVersion,
                    current.Version);
            }

            return await _store.UpdateTaskAsync(new UpdateTaskRequest
            {
                TaskId = taskId,
                ExpectedVersion = expectedVersion,
                Title = title,
                Description = description,
                AcceptanceCriteria = acceptanceCriteria,
                Priority = priority,
                ExecutionWindow = executionWindow,
                PreferredAgentId = preferredAgentId,
                TaskType = taskType,
                RequiredCapabilityIds = requiredCapabilityIds,
                RequiredProviderId = requiredProviderId,
                RequiredModelId = requiredModelId,
                AllowAgentFallback = allowAgentFallback,
                AutoDispatchEnabled = autoDispatchEnabled,
                NotBeforeUtc = notBeforeUtc,
                DueAtUtc = dueAtUtc,
                SortOrder = sortOrder,
                UpdatedBy = updatedBy,
            }, ct);
        }

        // 显式状态迁移：严格 CanTransition 校验（终态出边天然禁止，Failed→Ready 只能 Reopen）。
        var target = status.Value;
        if (!TaskStateMachine.CanTransition(current.Status, target))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                $"Task '{taskId}' cannot transition from '{current.Status}' to '{target}'.",
                taskId,
                expectedVersion,
                current.Version);
        }

        // A Task that has entered execution may only complete through the
        // evidence-bearing task disposition / Task-bound Goal settlement path.
        // Generic board metadata updates cannot manufacture execution success.
        if (target == WorkspaceTaskStatus.Completed
            && current.ActiveAssignmentId is not null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                $"Task '{taskId}' has an active assignment and must complete through the canonical task completion path.",
                taskId,
                expectedVersion,
                current.Version);
        }

        if (current.Version != expectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{taskId}' version conflict: expected {expectedVersion}, actual {current.Version}.",
                taskId,
                expectedVersion,
                current.Version);
        }

        var now = DateTimeOffset.UtcNow;
        var eventType = EventTypeForStatus(target);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.TaskId == taskId && t.WorkspaceId == workspaceId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId,
                expectedVersion,
                null);

        // 防御性二次 CAS（与读取同一上下文；单写者语义下与首次判定等价）。
        if (entity.Version != expectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{taskId}' version conflict: expected {expectedVersion}, actual {entity.Version}.",
                taskId,
                expectedVersion,
                entity.Version);
        }

        ApplyFieldUpdates(entity, title, description, acceptanceCriteria, priority, executionWindow,
            preferredAgentId, notBeforeUtc, dueAtUtc, sortOrder, taskType, requiredCapabilityIds,
            requiredProviderId, requiredModelId, allowAgentFallback);
        if (autoDispatchEnabled.HasValue)
            entity.AutoDispatchEnabled = autoDispatchEnabled.Value;

        entity.Status = target;
        entity.Version += 1;
        entity.UpdatedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(updatedBy))
        {
            entity.UpdatedBy = updatedBy;
        }

        switch (target)
        {
            case WorkspaceTaskStatus.Completed:
                entity.CompletedAtUtc = now;
                break;
            case WorkspaceTaskStatus.Failed:
                entity.FailedAtUtc = now;
                break;
            case WorkspaceTaskStatus.Archived:
                entity.ArchivedAtUtc = now;
                break;
        }

        var nextSequence = await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = nextSequence + 1,
            EventType = eventType,
            DecisionCode = target == WorkspaceTaskStatus.Completed
                ? "manual_without_execution"
                : null,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);

        return (await _store.GetTaskAsync(workspaceId, taskId, ct))!;
    }

    /// <summary>命令原子流程（Assign/RunNow/Cancel/Reopen/Archive/MarkFailed/Resume/Requeue）。</summary>
    public async Task<WorkspaceTask> ApplyCommandAsync(
        string workspaceId,
        string taskId,
        TaskCommand command,
        int expectedVersion,
        string? agentId = null,
        string? windowDecision = null,
        string? reason = null,
        string? updatedBy = null,
        CancellationToken ct = default)
    {
        var current = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (current is null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId,
                expectedVersion,
                null);
        }

        if (!TaskStateMachine.TryApplyCommand(current.Status, command, out var next))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                $"Command '{command}' is not valid for task '{taskId}' in status '{current.Status}'.",
                taskId,
                expectedVersion,
                current.Version);
        }

        if (current.Version != expectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{taskId}' version conflict: expected {expectedVersion}, actual {current.Version}.",
                taskId,
                expectedVersion,
                current.Version);
        }

        var now = DateTimeOffset.UtcNow;
        var eventType = EventTypeFor(command);
        var assignmentId = command is TaskCommand.Assign or TaskCommand.RunNow
            ? Guid.NewGuid().ToString("N")
            : null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.TaskId == taskId && t.WorkspaceId == workspaceId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId,
                expectedVersion,
                null);

        // 防御性二次 CAS（与读取同一上下文；单写者语义下与首次判定等价）。
        if (entity.Version != expectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{taskId}' version conflict: expected {expectedVersion}, actual {entity.Version}.",
                taskId,
                expectedVersion,
                entity.Version);
        }

        entity.Status = next;
        entity.Version += 1;
        entity.UpdatedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(updatedBy))
        {
            entity.UpdatedBy = updatedBy;
        }

        switch (command)
        {
            case TaskCommand.Assign or TaskCommand.RunNow:
            {
                ArgumentNullException.ThrowIfNull(agentId);
                var attemptNumber = await db.TaskAssignmentAttempts
                    .Where(a => a.TaskId == taskId)
                    .MaxAsync(a => (int?)a.AttemptNumber, ct) ?? 0;

                var attempt = new TaskAssignmentAttemptEntity
                {
                    AttemptId = assignmentId!,
                    TaskId = taskId,
                    WorkspaceId = workspaceId,
                    AgentId = agentId,
                    AttemptNumber = attemptNumber + 1,
                    Status = AssignmentAttemptStatus.Reserved,
                    WindowDecision = windowDecision,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ActiveAtUtc = null,
                    ReleasedAtUtc = null,
                };
                db.TaskAssignmentAttempts.Add(attempt);
                entity.ActiveAssignmentId = attempt.AttemptId;

                // ── TB-05：同事务追加 DispatchOutbox（不变量 #6，外部发送不在此发生，不变量 #7）──
                var idempotencyKey = TaskDispatchIds.BuildIdempotencyKey(taskId, attempt.AttemptId);
                var envelope = new TaskInstructionEnvelope
                {
                    IdempotencyKey = idempotencyKey,
                    WorkspaceId = workspaceId,
                    TaskId = taskId,
                    AssignmentId = attempt.AttemptId,
                    AgentId = agentId,
                    Origin = TaskInstructionEnvelope.OriginTaskManual,
                    Priority = TaskWireMaps.PriorityToString(entity.Priority),
                    ExecutionWindow = TaskWireMaps.ExecutionWindowToString(entity.ExecutionWindow),
                    Title = entity.Title,
                    Description = entity.Description,
                    AcceptanceCriteria = entity.AcceptanceCriteria,
                };
                db.TaskDispatchOutbox.Add(new TaskDispatchOutboxEntity
                {
                    IdempotencyKey = idempotencyKey,
                    WorkspaceId = workspaceId,
                    TaskId = taskId,
                    AssignmentId = attempt.AttemptId,
                    AgentId = agentId,
                    Origin = TaskInstructionEnvelope.OriginTaskManual,
                    EnvelopePayload = TaskDispatchSerialization.Serialize(envelope),
                    Status = TaskDispatchOutboxStatuses.Pending,
                    AttemptCount = 0,
                    CreatedAtUtc = now,
                });
                break;
            }
            case TaskCommand.Cancel:
            {
                if (entity.ActiveAssignmentId is not null)
                {
                    var active = await db.TaskAssignmentAttempts
                        .SingleOrDefaultAsync(a => a.AttemptId == entity.ActiveAssignmentId, ct);
                    if (active is not null)
                    {
                        active.ReleasedAtUtc = now;
                        active.UpdatedAtUtc = now;
                    }
                }

                entity.ActiveAssignmentId = null;
                break;
            }
            case TaskCommand.Archive:
                entity.ArchivedAtUtc = now;
                break;
            case TaskCommand.MarkFailed:
                entity.FailedAtUtc = now;
                if (entity.ActiveAssignmentId is not null)
                {
                    var active = await db.TaskAssignmentAttempts
                        .SingleOrDefaultAsync(a => a.AttemptId == entity.ActiveAssignmentId, ct);
                    if (active is not null)
                    {
                        active.Status = AssignmentAttemptStatus.Failed;
                        active.ReleasedAtUtc = now;
                        active.UpdatedAtUtc = now;
                    }

                    entity.ActiveAssignmentId = null;
                }
                if (reason is not null)
                {
                    entity.FailureReason = reason;
                }

                break;
        }

        var nextSequence = await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = nextSequence + 1,
            EventType = eventType,
            AssignmentId = assignmentId,
            AgentId = agentId,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);

        return current with
        {
            Status = next,
            Version = current.Version + 1,
            UpdatedAtUtc = now,
            UpdatedBy = updatedBy ?? current.UpdatedBy,
            ActiveAssignmentId = entity.ActiveAssignmentId,
            ArchivedAtUtc = entity.ArchivedAtUtc,
            FailedAtUtc = entity.FailedAtUtc,
            FailureReason = entity.FailureReason,
        };
    }

    /// <summary>
    /// 智能删除：无历史 Backlog 任务走硬删（返回 null），其余任意状态任务归档软删（返回归档后的任务）。
    /// <para>
    /// 删除是用户侧「移除无效任务」语义，不带 CAS（目标即移除，无需乐观锁）。归档路径从任意状态原子迁移到
    /// Archived：version+1、写 task.archived 事件、释放活跃 Assignment（同 Cancel 语义），保留完整审计历史。
    /// 已归档任务幂等成功（不重复写事件）。
    /// </para>
    /// </summary>
    public async Task<WorkspaceTask?> DeleteTaskAsync(
        string workspaceId,
        string taskId,
        string? updatedBy = null,
        CancellationToken ct = default)
    {
        var current = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (current is null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId);
        }

        // 无历史 Backlog → 硬删（保留既有审计语义与 HardDeleteTaskAsync 判定）。
        if (await _store.HardDeleteTaskAsync(workspaceId, taskId, ct))
        {
            return null;
        }

        // 已归档 → 幂等成功。
        if (current.Status == WorkspaceTaskStatus.Archived)
        {
            return current;
        }

        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.TaskId == taskId && t.WorkspaceId == workspaceId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId);

        // 释放活跃 Assignment（与 Cancel 命令一致）。
        if (entity.ActiveAssignmentId is not null)
        {
            var active = await db.TaskAssignmentAttempts
                .SingleOrDefaultAsync(a => a.AttemptId == entity.ActiveAssignmentId, ct);
            if (active is not null)
            {
                active.ReleasedAtUtc = now;
                active.UpdatedAtUtc = now;
            }

            entity.ActiveAssignmentId = null;
        }

        entity.Status = WorkspaceTaskStatus.Archived;
        entity.Version += 1;
        entity.UpdatedAtUtc = now;
        entity.ArchivedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(updatedBy))
        {
            entity.UpdatedBy = updatedBy;
        }

        var nextSequence = await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = nextSequence + 1,
            EventType = TaskEventType.TaskArchived,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);

        return (await _store.GetTaskAsync(workspaceId, taskId, ct))!;
    }

    private static TaskEventType EventTypeFor(TaskCommand command) => command switch
    {
        TaskCommand.Assign or TaskCommand.RunNow => TaskEventType.TaskReserved,
        TaskCommand.Cancel => TaskEventType.TaskCancelled,
        TaskCommand.Reopen => TaskEventType.TaskReopened,
        TaskCommand.Archive => TaskEventType.TaskArchived,
        TaskCommand.MarkFailed => TaskEventType.TaskFailed,
        TaskCommand.Resume or TaskCommand.Requeue => TaskEventType.TaskReady,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "未知任务命令。"),
    };

    /// <summary>PATCH 状态迁移目标 → canonical event type.</summary>
    private static TaskEventType EventTypeForStatus(WorkspaceTaskStatus target) => target switch
    {
        WorkspaceTaskStatus.Ready => TaskEventType.TaskReady,
        WorkspaceTaskStatus.Completed => TaskEventType.TaskCompleted,
        WorkspaceTaskStatus.Failed => TaskEventType.TaskFailed,
        WorkspaceTaskStatus.Cancelled => TaskEventType.TaskCancelled,
        WorkspaceTaskStatus.Archived => TaskEventType.TaskArchived,
        _ => TaskEventType.TaskUpdated,
    };

    private static void ApplyFieldUpdates(
        WorkspaceTaskEntity entity,
        string? title,
        string? description,
        string? acceptanceCriteria,
        TaskPriority? priority,
        TaskExecutionWindow? executionWindow,
        string? preferredAgentId,
        DateTimeOffset? notBeforeUtc,
        DateTimeOffset? dueAtUtc,
        long? sortOrder,
        string? taskType,
        IReadOnlyList<string>? requiredCapabilityIds,
        string? requiredProviderId,
        string? requiredModelId,
        bool? allowAgentFallback)
    {
        if (title is not null) entity.Title = title;
        if (description is not null) entity.Description = description;
        if (acceptanceCriteria is not null) entity.AcceptanceCriteria = acceptanceCriteria;
        if (priority.HasValue) entity.Priority = priority.Value;
        if (executionWindow.HasValue) entity.ExecutionWindow = executionWindow.Value;
        if (preferredAgentId is not null) entity.PreferredAgentId = preferredAgentId;
        if (taskType is not null) entity.TaskType = TaskRoutingMetadata.NormalizeTaskType(taskType);
        if (requiredCapabilityIds is not null)
        {
            entity.RequiredCapabilitiesJson = JsonSerializer.Serialize(
                TaskRoutingMetadata.NormalizeCapabilityIds(requiredCapabilityIds));
        }
        if (requiredProviderId is not null)
            entity.RequiredProviderId = TaskRoutingMetadata.NormalizeOptionalIdentifier(requiredProviderId, 64, "requiredProviderId");
        if (requiredModelId is not null)
            entity.RequiredModelId = TaskRoutingMetadata.NormalizeOptionalIdentifier(requiredModelId, 128, "requiredModelId");
        if (allowAgentFallback.HasValue) entity.AllowAgentFallback = allowAgentFallback.Value;
        if (notBeforeUtc.HasValue) entity.NotBeforeUtc = notBeforeUtc.Value;
        if (dueAtUtc.HasValue) entity.DueAtUtc = dueAtUtc.Value;
        if (sortOrder.HasValue) entity.SortOrder = sortOrder.Value;
    }

}
