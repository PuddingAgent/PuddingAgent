using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-06: Agent Task 命令服务（ClaimAsync / ApplyDispositionAsync / ListMineAsync / GetAsync）。
/// <para>
/// 复用 <see cref="TaskStateMachine.TryInterpretDisposition"/> + TB-03 原子模式
/// （<see cref="IDbContextFactory{TContext}"/> 单 SaveChanges 提交 Task + Attempt + Event + Binding 回填，
/// 不变量 #6）。不修改 TB-01 冻结的 <see cref="ITaskStore"/>。claim 与 update(accept) 共用
/// <see cref="ClaimAsync"/> 单路径，<c>task.accepted</c> 事件仅 Assigned→InProgress 时产生一次（状态守卫）。
/// </para>
/// </summary>
public sealed class TaskAgentCommandService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkAdmissionFence fence) : ITaskAgentCommandService
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory = dbFactory;
    private readonly IWorkAdmissionFence _fence = fence;

    // ── 查询 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAgentListResult> ListMineAsync(TaskAgentListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit < 1 || query.Limit > 100)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidCursor,
                "limit must be between 1 and 100.");
        }

        if (query.Status is not null && query.BoardColumn is not null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                "status and board_column are mutually exclusive; provide only one.");
        }

        WorkspaceTaskStatus? status = query.Status is null ? null : TaskWireMaps.StatusFromString(query.Status);
        TaskPriority? priority = query.Priority is null ? null : TaskWireMaps.PriorityFromString(query.Priority);
        IReadOnlyList<WorkspaceTaskStatus>? boardStatuses = query.BoardColumn is null
            ? null
            : TaskWireMaps.BoardColumnToStatuses(TaskWireMaps.BoardColumnFromString(query.BoardColumn));

        var cursor = ParseCursor(query.Cursor);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // mine：active_assignment_id 非空，且对应 attempt.agent_id == me（不按 released_at_utc 过滤，保证 Completed 历史可见）。
        var mine = db.WorkspaceTasks
            .AsNoTracking()
            .Where(t => t.WorkspaceId == query.WorkspaceId && t.ActiveAssignmentId != null)
            .Join(
                db.TaskAssignmentAttempts.AsNoTracking().Where(a => a.AgentId == query.AgentId),
                t => t.ActiveAssignmentId,
                a => a.AttemptId,
                (t, _) => t);

        // 默认排除 Archived/Cancelled；显式 status 或 board_column 过滤时按过滤条件（历史筛选语义）。
        if (status.HasValue)
        {
            mine = mine.Where(t => t.Status == status.Value);
        }
        else if (boardStatuses is not null)
        {
            mine = mine.Where(t => boardStatuses.Contains(t.Status));
        }
        else
        {
            mine = mine.Where(t => t.Status != WorkspaceTaskStatus.Archived
                                   && t.Status != WorkspaceTaskStatus.Cancelled);
        }

        if (priority.HasValue)
        {
            mine = mine.Where(t => t.Priority == priority.Value);
        }

        var ordered = await mine
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.TaskId)
            .ToListAsync(ct);

        // keyset 游标（sortOrder|taskId）。
        IEnumerable<WorkspaceTaskEntity> scoped = ordered;
        if (cursor is not null)
        {
            scoped = ordered.Where(t =>
                t.SortOrder > cursor.Value.SortOrder
                || (t.SortOrder == cursor.Value.SortOrder
                    && string.CompareOrdinal(t.TaskId, cursor.Value.TaskId) > 0));
        }

        var all = scoped.ToList();
        var total = all.Count;
        var page = all.Take(query.Limit).ToList();
        string? nextCursor = null;
        if (page.Count > 0 && page.Count < total)
        {
            var last = page[^1];
            nextCursor = $"{last.SortOrder.ToString(CultureInfo.InvariantCulture)}|{last.TaskId}";
        }

        return new TaskAgentListResult
        {
            Total = total,
            NextCursor = nextCursor,
            Items = page.Select(ToListItem).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<TaskAgentGetResult?> GetAsync(
        string workspaceId,
        string taskId,
        string agentId,
        int eventsLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.WorkspaceTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.TaskId == taskId, ct);
        if (task is null || task.ActiveAssignmentId is null)
        {
            return null;
        }

        // mine 校验（非 mine 与不存在统一返回 null，信息隐藏）。
        var isMine = await db.TaskAssignmentAttempts
            .AsNoTracking()
            .AnyAsync(a => a.AttemptId == task.ActiveAssignmentId && a.AgentId == agentId, ct);
        if (!isMine)
        {
            return null;
        }

        var allowedTransitions = TaskStateMachine.GetAllowedTransitions(task.Status)
            .Select(TaskWireMaps.StatusToString)
            .ToList();

        var allowedDispositions = Enum.GetValues<TaskDisposition>()
            .Where(d => TaskStateMachine.TryInterpretDisposition(task.Status, d, out _))
            .Select(DispositionToString)
            .ToList();

        var assignment = await BuildAssignmentSummaryAsync(db, task, ct);

        var events = await db.TaskEvents
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.Sequence)
            .Take(Math.Clamp(eventsLimit, 1, 100))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return new TaskAgentGetResult
        {
            Task = ToTaskDetail(task),
            AllowedTransitions = allowedTransitions,
            AllowedDispositions = allowedDispositions,
            ActiveAssignment = assignment,
            RecentEvents = events.Select(ToEventSummary).ToList(),
        };
    }

    // ── 命令 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAgentMutationResult> ClaimAsync(TaskAgentClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.WorkspaceId == request.WorkspaceId && t.TaskId == request.TaskId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{request.TaskId}' not found.",
                request.TaskId,
                request.ExpectedVersion,
                null);

        // 幂等 no-op：已 InProgress 且 active assignment 相同（claim 与 accept 重复调用）。
        if (task.Status == WorkspaceTaskStatus.InProgress && task.ActiveAssignmentId == request.AssignmentId)
        {
            return BuildMutationResult(task, request.AssignmentId, "accept", "task.accepted", "InProgress");
        }

        // 迟到调用三守卫：stale assignment / version / state。
        if (task.ActiveAssignmentId != request.AssignmentId)
        {
            throw new TaskStoreException(
                TaskErrorCode.AssignmentStale,
                $"Assignment '{request.AssignmentId}' is not the active assignment for task '{request.TaskId}'.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        if (task.Version != request.ExpectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{request.TaskId}' version conflict: expected {request.ExpectedVersion}, actual {task.Version}.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        if (task.Status != WorkspaceTaskStatus.Assigned)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskStateConflict,
                $"Task '{request.TaskId}' is not Assigned (current: {TaskWireMaps.StatusToString(task.Status)}).",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        // Fence(execute) 检查点：本阶段 ManualAlwaysAllowFence 恒 allow。
        var decision = await _fence.EvaluateAsync(new WorkAdmissionFenceInput
        {
            WorkspaceId = request.WorkspaceId,
            TaskId = request.TaskId,
            AssignmentId = request.AssignmentId,
            AgentId = request.AgentId,
            TaskStatus = task.Status,
            Priority = task.Priority,
            ExecutionWindow = task.ExecutionWindow,
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
        }, ct);
        if (decision.Verdict != FenceVerdict.Allow)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskStateConflict,
                $"Task '{request.TaskId}' not admitted by fence: {decision.Code}.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        var now = DateTimeOffset.UtcNow;
        task.Status = WorkspaceTaskStatus.InProgress;
        task.Version += 1;
        task.UpdatedAtUtc = now;

        var attempt = await db.TaskAssignmentAttempts
            .SingleOrDefaultAsync(a => a.AttemptId == request.AssignmentId, ct);
        if (attempt is not null && attempt.Status == AssignmentAttemptStatus.Assigned)
        {
            attempt.Status = AssignmentAttemptStatus.InProgress;
            attempt.UpdatedAtUtc = now;
        }

        await AppendEventAsync(db, task, request.AgentId, request.AssignmentId,
            request.ExecutionId, request.SessionId, request.TraceId, TaskEventType.TaskAccepted, now, ct);
        await BackfillBindingAsync(db, task.TaskId, request.AssignmentId, request.ExecutionId, request.SessionId, ct);

        await db.SaveChangesAsync(ct);

        return BuildMutationResult(task, request.AssignmentId, "accept", "task.accepted",
            attempt?.Status.ToString() ?? AssignmentAttemptStatus.InProgress.ToString());
    }

    /// <inheritdoc />
    public async Task<TaskAgentMutationResult> ApplyDispositionAsync(TaskAgentUpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var disposition = ParseDisposition(request.Disposition);

        // claim 与 update(accept) 共享单路径。
        if (disposition == TaskDisposition.Accept)
        {
            return await ClaimAsync(new TaskAgentClaimRequest
            {
                WorkspaceId = request.WorkspaceId,
                TaskId = request.TaskId,
                AssignmentId = request.AssignmentId,
                ExpectedVersion = request.ExpectedVersion,
                AgentId = request.AgentId,
                ExecutionId = request.ExecutionId,
                SessionId = request.SessionId,
                TraceId = request.TraceId,
            }, ct);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.WorkspaceId == request.WorkspaceId && t.TaskId == request.TaskId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{request.TaskId}' not found.",
                request.TaskId,
                request.ExpectedVersion,
                null);

        if (task.ActiveAssignmentId != request.AssignmentId)
        {
            throw new TaskStoreException(
                TaskErrorCode.AssignmentStale,
                $"Assignment '{request.AssignmentId}' is not the active assignment for task '{request.TaskId}'.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        if (task.Version != request.ExpectedVersion)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{request.TaskId}' version conflict: expected {request.ExpectedVersion}, actual {task.Version}.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        // 已知 disposition 但不适用于当前状态 → state_conflict（迟到调用拒绝）。
        if (!TaskStateMachine.TryInterpretDisposition(task.Status, disposition, out var next))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskStateConflict,
                $"Disposition '{request.Disposition}' is not valid for task '{request.TaskId}' in status '{TaskWireMaps.StatusToString(task.Status)}'.",
                request.TaskId,
                request.ExpectedVersion,
                task.Version);
        }

        var now = DateTimeOffset.UtcNow;
        var eventType = EventTypeForDisposition(disposition);

        // ── Task 字段与状态推进 ──
        switch (disposition)
        {
            case TaskDisposition.Progress:
                task.ProgressPercent = request.ProgressPercent;
                task.ProgressSummary = request.ProgressSummary;
                break;
            case TaskDisposition.Blocked:
                task.BlockerKind = "agent_requested";
                task.BlockerReason = request.Reason;
                break;
            case TaskDisposition.NeedsApproval:
                task.BlockerKind = "approval_required";
                task.BlockerReason = request.Reason;
                break;
            case TaskDisposition.Todo:
                task.ActiveAssignmentId = null;
                break;
            case TaskDisposition.Rejected:
                task.ActiveAssignmentId = null;
                break;
            case TaskDisposition.Completed:
                task.CompletedAtUtc = now;
                task.ActiveAssignmentId = null;
                break;
        }

        task.Status = next;
        task.Version += 1;
        task.UpdatedAtUtc = now;

        // ── Attempt 推进 ──
        var attempt = await db.TaskAssignmentAttempts
            .SingleOrDefaultAsync(a => a.AttemptId == request.AssignmentId, ct);
        switch (disposition)
        {
            case TaskDisposition.Progress:
            case TaskDisposition.Blocked:
            case TaskDisposition.NeedsApproval:
                if (attempt is not null)
                {
                    attempt.UpdatedAtUtc = now;
                }

                break;
            case TaskDisposition.Todo:
                if (attempt is not null)
                {
                    attempt.ReleasedAtUtc = now;
                    attempt.UpdatedAtUtc = now;
                }

                break;
            case TaskDisposition.Rejected:
                if (attempt is not null)
                {
                    attempt.Status = AssignmentAttemptStatus.Rejected;
                    attempt.ReleasedAtUtc = now;
                    attempt.UpdatedAtUtc = now;
                }

                break;
            case TaskDisposition.Completed:
                if (attempt is not null)
                {
                    attempt.Status = AssignmentAttemptStatus.Completed;
                    attempt.ReleasedAtUtc = now;
                    attempt.UpdatedAtUtc = now;
                }

                break;
        }

        await AppendEventAsync(db, task, request.AgentId, request.AssignmentId,
            request.ExecutionId, request.SessionId, request.TraceId, eventType, now, ct);
        await BackfillBindingAsync(db, task.TaskId, request.AssignmentId, request.ExecutionId, request.SessionId, ct);

        await db.SaveChangesAsync(ct);

        return BuildMutationResult(task, request.AssignmentId, request.Disposition,
            TaskWireMaps.EventTypeToString(eventType), attempt?.Status.ToString() ?? string.Empty);
    }

    // ── 映射与帮助 ──────────────────────────────────────────

    private static async Task AppendEventAsync(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        string? agentId,
        string assignmentId,
        string? executionId,
        string? sessionId,
        string? traceId,
        TaskEventType eventType,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var nextSequence = await db.TaskEvents
            .Where(e => e.TaskId == task.TaskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = nextSequence + 1,
            EventType = eventType,
            AssignmentId = assignmentId,
            AgentId = agentId,
            ExecutionId = executionId,
            SessionId = sessionId,
            TraceId = traceId,
            CreatedAtUtc = now,
        });
    }

    private static async Task BackfillBindingAsync(
        PlatformDbContext db,
        string taskId,
        string assignmentId,
        string? executionId,
        string? sessionId,
        CancellationToken ct)
    {
        if (executionId is null && sessionId is null)
        {
            return;
        }

        var binding = await db.TaskExecutionBindings
            .SingleOrDefaultAsync(b => b.TaskId == taskId && b.AssignmentId == assignmentId, ct);
        if (binding is null)
        {
            return;
        }

        // 幂等回填：仅在 null 时写入（重复 update 不覆盖）。
        if (executionId is not null && binding.ExecutionId is null)
        {
            binding.ExecutionId = executionId;
        }

        if (sessionId is not null && binding.SessionId is null)
        {
            binding.SessionId = sessionId;
        }
    }

    private static async Task<TaskAgentAssignmentSummary?> BuildAssignmentSummaryAsync(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        CancellationToken ct)
    {
        if (task.ActiveAssignmentId is null)
        {
            return null;
        }

        var attempt = await db.TaskAssignmentAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.AttemptId == task.ActiveAssignmentId, ct);
        if (attempt is null)
        {
            return null;
        }

        var binding = await db.TaskExecutionBindings
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.TaskId == task.TaskId && b.AssignmentId == task.ActiveAssignmentId, ct);

        return new TaskAgentAssignmentSummary
        {
            AssignmentId = attempt.AttemptId,
            AgentId = attempt.AgentId,
            Status = attempt.Status.ToString(),
            RejectionReason = null,
            DeliveryId = binding?.DeliveryId,
            ExecutionId = binding?.ExecutionId,
            SessionId = binding?.SessionId,
            RunId = null,
            TraceId = null,
        };
    }

    private static TaskAgentMutationResult BuildMutationResult(
        WorkspaceTaskEntity task,
        string assignmentId,
        string disposition,
        string eventWire,
        string assignmentStatus)
        => new()
        {
            TaskId = task.TaskId,
            Disposition = disposition,
            Status = TaskWireMaps.StatusToString(task.Status),
            Version = task.Version,
            AssignmentId = assignmentId,
            AssignmentStatus = assignmentStatus,
            Event = eventWire,
            BoardColumn = TaskWireMaps.BoardColumnToString(TaskStateMachine.ProjectBoardColumn(task.Status)),
            BlockerKind = task.BlockerKind,
            BlockerReason = task.BlockerReason,
            ProgressPercent = task.ProgressPercent,
            ProgressSummary = task.ProgressSummary,
        };

    private static TaskAgentListItem ToListItem(WorkspaceTaskEntity t)
        => new()
        {
            TaskId = t.TaskId,
            Title = t.Title,
            Status = TaskWireMaps.StatusToString(t.Status),
            BoardColumn = SafeProjectBoardColumn(t.Status),
            Priority = TaskWireMaps.PriorityToString(t.Priority),
            ExecutionWindow = TaskWireMaps.ExecutionWindowToString(t.ExecutionWindow),
            ActiveAssignmentId = t.ActiveAssignmentId,
            ProgressPercent = t.ProgressPercent,
            DueAtUtc = t.DueAtUtc,
            UpdatedAtUtc = t.UpdatedAtUtc,
            Version = t.Version,
        };

    private static TaskAgentTaskDetail ToTaskDetail(WorkspaceTaskEntity t)
        => new()
        {
            TaskId = t.TaskId,
            WorkspaceId = t.WorkspaceId,
            Title = t.Title,
            Description = t.Description,
            AcceptanceCriteria = t.AcceptanceCriteria,
            Status = TaskWireMaps.StatusToString(t.Status),
            BoardColumn = SafeProjectBoardColumn(t.Status),
            Archived = t.Status is WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived,
            Priority = TaskWireMaps.PriorityToString(t.Priority),
            ExecutionWindow = TaskWireMaps.ExecutionWindowToString(t.ExecutionWindow),
            PreferredAgentId = t.PreferredAgentId,
            TaskType = t.TaskType,
            RequiredCapabilityIds = DeserializeCapabilities(t.RequiredCapabilitiesJson),
            RequiredProviderId = t.RequiredProviderId,
            RequiredModelId = t.RequiredModelId,
            AllowAgentFallback = t.AllowAgentFallback,
            AutoDispatchEnabled = t.AutoDispatchEnabled,
            ActiveAssignmentId = t.ActiveAssignmentId,
            NotBeforeUtc = t.NotBeforeUtc,
            DueAtUtc = t.DueAtUtc,
            NextEligibleAtUtc = t.NextEligibleAtUtc,
            SortOrder = t.SortOrder,
            ProgressPercent = t.ProgressPercent,
            ProgressSummary = t.ProgressSummary,
            BlockerKind = t.BlockerKind,
            BlockerReason = t.BlockerReason,
            FailureCode = t.FailureCode,
            FailureReason = t.FailureReason,
            Origin = t.Origin.HasValue ? TaskWireMaps.OriginToString(t.Origin.Value) : null,
            Version = t.Version,
            CreatedBy = t.CreatedBy,
            UpdatedBy = t.UpdatedBy,
            CreatedAtUtc = t.CreatedAtUtc,
            UpdatedAtUtc = t.UpdatedAtUtc,
            CompletedAtUtc = t.CompletedAtUtc,
            FailedAtUtc = t.FailedAtUtc,
            ArchivedAtUtc = t.ArchivedAtUtc,
        };

    private static TaskAgentEventSummary ToEventSummary(TaskEventEntity e)
        => new()
        {
            EventId = e.EventId,
            Sequence = e.Sequence,
            EventType = TaskWireMaps.EventTypeToString(e.EventType),
            AssignmentId = e.AssignmentId,
            CreatedAtUtc = e.CreatedAtUtc,
        };

    private static string? SafeProjectBoardColumn(WorkspaceTaskStatus status)
        => status is WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived
            ? null
            : TaskWireMaps.BoardColumnToString(TaskStateMachine.ProjectBoardColumn(status));

    private static IReadOnlyList<string> DeserializeCapabilities(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TaskEventType EventTypeForDisposition(TaskDisposition disposition) => disposition switch
    {
        TaskDisposition.Accept => TaskEventType.TaskAccepted,
        TaskDisposition.Progress => TaskEventType.TaskProgressed,
        TaskDisposition.Todo => TaskEventType.TaskReady,
        TaskDisposition.Blocked or TaskDisposition.NeedsApproval => TaskEventType.TaskBlocked,
        TaskDisposition.Rejected => TaskEventType.TaskAssignmentRejected,
        TaskDisposition.Completed => TaskEventType.TaskCompleted,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "未知 disposition。"),
    };

    private static string DispositionToString(TaskDisposition disposition) => disposition switch
    {
        TaskDisposition.Accept => "accept",
        TaskDisposition.Progress => "progress",
        TaskDisposition.Todo => "todo",
        TaskDisposition.Blocked => "blocked",
        TaskDisposition.NeedsApproval => "needs_approval",
        TaskDisposition.Rejected => "rejected",
        TaskDisposition.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "未知 disposition。"),
    };

    private static TaskDisposition ParseDisposition(string? value) => value switch
    {
        "accept" => TaskDisposition.Accept,
        "progress" => TaskDisposition.Progress,
        "todo" => TaskDisposition.Todo,
        "blocked" => TaskDisposition.Blocked,
        "needs_approval" => TaskDisposition.NeedsApproval,
        "rejected" => TaskDisposition.Rejected,
        "completed" => TaskDisposition.Completed,
        _ => throw new TaskStoreException(
            TaskErrorCode.TaskInvalidDisposition,
            $"Unknown disposition wire value '{value}'."),
    };

    private static (long SortOrder, string TaskId)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var parts = cursor.Split('|', 2);
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder)
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidCursor,
                $"Invalid cursor '{cursor}'. Expected format '{{sortOrder}}|{{taskId}}'.");
        }

        return (sortOrder, parts[1]);
    }
}
