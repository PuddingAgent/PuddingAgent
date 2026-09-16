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

        // Card 4ed930e7-②：完成事实唯一路径防护。执行中任务（Assigned/InProgress，无论
        // 是否仍持有 active assignment——含 orphaned claim 形态）不得经通用 PATCH 写成
        // Completed；完成只能经 TaskAgentCommandService disposition / 完成结算服务
        // （TaskCompletionSettlementService）产生。凡持有 active assignment 的其他状态
        // 同样拒绝，防止未来状态表扩张时旁路回潮。
        if (target == WorkspaceTaskStatus.Completed
            && (current.Status is WorkspaceTaskStatus.Assigned or WorkspaceTaskStatus.InProgress
                || current.ActiveAssignmentId is not null))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                $"Task '{taskId}' is in execution status '{current.Status}' and must complete through the canonical task completion path (task disposition / completion settlement), not a generic PATCH.",
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
        CancellationToken ct = default,
        bool force = false)
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

        // Stage 2（D2/D4）：容器母卡语义。归档/取消母卡时若存在未终态子卡，
        // 默认 fail-closed 拒绝（task.has_non_terminal_children）；显式 force 才级联。
        // 只读快照：本判定不写库、不改任何 Status（D3 禁止母卡状态从子卡派生）。
        List<WorkspaceTaskEntity> children = force
            ? []
            : await ListChildrenAsync(workspaceId, taskId, ct);
        if (command is TaskCommand.Cancel or TaskCommand.Archive)
        {
            var rejection = TaskHierarchyRules.ValidateArchiveOrCancel(taskId, ToHierarchyViews(children), force);
            if (rejection is not null)
            {
                var counts = TaskHierarchyRules.CountChildren(taskId, ToHierarchyViews(children));
                throw new TaskStoreException(
                    rejection.Value,
                    $"Task '{taskId}' still has {counts.NonTerminal} non-terminal child task(s); pass force=true to cascade cancel them before "
                    + $"{(command == TaskCommand.Archive ? "archive" : "cancel")}.",
                    taskId,
                    expectedVersion,
                    current.Version);
            }
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

        // Stage 2（D4）级联：先把母卡的未终态子卡走 cancel（单层：子卡即叶子），再归档/取消母卡。
        // 级联与母卡转换在同一 SaveChanges 内原子提交；级联只做状态迁移 + 事件，绝不做硬删。
        if (force && command is TaskCommand.Cancel or TaskCommand.Archive)
        {
            await CascadeCancelNonTerminalChildrenAsync(db, workspaceId, taskId, now, updatedBy, ct);
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
        CancellationToken ct = default,
        bool force = false)
    {
        var current = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (current is null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{taskId}' not found.",
                taskId);
        }

        // Stage 2（D4）：有子卡的母卡——fail-closed 拒绝或显式 force 级联；
        // 两条路都<b>禁止硬删</b>（硬删会让子卡 parent_task_id 悬空，破坏层级事实）。
        List<WorkspaceTaskEntity> children = force
            ? []
            : await ListChildrenAsync(workspaceId, taskId, ct);
        if (children.Count > 0)
        {
            var rejection = TaskHierarchyRules.ValidateArchiveOrCancel(taskId, ToHierarchyViews(children), force);
            if (rejection is not null)
            {
                var counts = TaskHierarchyRules.CountChildren(taskId, ToHierarchyViews(children));
                throw new TaskStoreException(
                    rejection.Value,
                    $"Task '{taskId}' still has {counts.NonTerminal} non-terminal child task(s); pass force=true to cascade cancel them before delete.",
                    taskId);
            }
        }

        // 无历史 Backlog 且无子卡 → 硬删（保留既有审计语义与 HardDeleteTaskAsync 判定）。
        if (children.Count == 0 && await _store.HardDeleteTaskAsync(workspaceId, taskId, ct))
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

        // Stage 2（D4）级联：force 时先把未终态子卡走 cancel（与母卡归档同一提交单元），
        // 然后母卡走归档（绝不硬删有子卡的任务）。
        if (force)
        {
            await CascadeCancelNonTerminalChildrenAsync(db, workspaceId, taskId, now, updatedBy, ct);
        }

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

    // ── Stage 2：母/子层级（D2/D3/D4）────────────────────────

    /// <summary>
    /// Stage 2：把 EF 实体投影成 Core 层级视图——<see cref="TaskHierarchyRules"/> 的唯一输入面。
    /// 只映射规则真正读取的字段（TaskId/Status/ParentTaskId），不写库、不派生状态（D3）。
    /// </summary>
    private static IReadOnlyList<WorkspaceTask> ToHierarchyViews(IReadOnlyList<WorkspaceTaskEntity> entities)
        => entities
            .Select(entity => new WorkspaceTask
            {
                TaskId = entity.TaskId,
                WorkspaceId = entity.WorkspaceId,
                Title = entity.Title,
                Status = entity.Status,
                ParentTaskId = entity.ParentTaskId,
            })
            .ToList();

    /// <summary>
    /// 只读读取母卡的直接子卡（容器判定与 fail-closed 门禁的输入）。
    /// 不做任何写入，也不触碰 <see cref="WorkspaceTaskStatus"/>（D3）。
    /// </summary>
    private async Task<List<WorkspaceTaskEntity>> ListChildrenAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkspaceTasks
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.ParentTaskId == taskId)
            .OrderBy(t => t.TaskId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// D4 显式级联：把母卡的未终态子卡逐张走 cancel（状态迁移 + task.cancelled 事件 +
    /// 释放活跃 assignment），与母卡的归档/取消在<b>同一 SaveChanges</b> 内原子提交。
    /// 单层级（D1）⇒ 子卡即叶子；级联只做状态迁移，<b>绝不硬删</b>。
    /// </summary>
    private static async Task CascadeCancelNonTerminalChildrenAsync(
        PlatformDbContext db,
        string workspaceId,
        string parentTaskId,
        DateTimeOffset now,
        string? updatedBy,
        CancellationToken ct)
    {
        var children = await db.WorkspaceTasks
            .Where(t => t.WorkspaceId == workspaceId && t.ParentTaskId == parentTaskId)
            .OrderBy(t => t.TaskId)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            if (TaskStateMachine.IsTerminal(child.Status))
            {
                continue;
            }

            child.Status = WorkspaceTaskStatus.Cancelled;
            child.Version += 1;
            child.UpdatedAtUtc = now;
            if (!string.IsNullOrWhiteSpace(updatedBy))
            {
                child.UpdatedBy = updatedBy;
            }

            if (child.ActiveAssignmentId is not null)
            {
                var active = await db.TaskAssignmentAttempts
                    .SingleOrDefaultAsync(a => a.AttemptId == child.ActiveAssignmentId, ct);
                if (active is not null)
                {
                    active.ReleasedAtUtc = now;
                    active.UpdatedAtUtc = now;
                }

                child.ActiveAssignmentId = null;
            }

            var sequence = await db.TaskEvents
                .Where(e => e.TaskId == child.TaskId)
                .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

            db.TaskEvents.Add(new TaskEventEntity
            {
                EventId = Guid.NewGuid().ToString("N"),
                TaskId = child.TaskId,
                WorkspaceId = workspaceId,
                Sequence = sequence + 1,
                EventType = TaskEventType.TaskCancelled,
                DecisionCode = "parent_cascade_cancel",
                CreatedAtUtc = now,
            });
        }
    }

}
