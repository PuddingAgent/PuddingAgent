using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-09: 管理者视角任务看板服务 — 实现 <see cref="IWorkspaceTaskAdminService"/>。
/// <para>
/// 复用 <see cref="SqliteWorkspaceTaskStore"/>（CRUD 持久化）+ <see cref="TaskCommandService"/>
/// （状态机校验 + CAS + Assignment + 事件原子提交）+ <see cref="TaskWireMaps"/>（wire ↔ 枚举），
/// 详情构造复用 <see cref="TaskAgentCommandService.GetAsync"/> 的等价映射逻辑。
/// </para>
/// <para>
/// 与 TB-06 的差异：无 mine 范围限制、无 Active Task Context 守卫、可跨 Agent 创建/查看/更新/
/// 删除/命令操作。服务被 Singleton 工具消费，因此构造仅依赖 Singleton 的
/// <see cref="IDbContextFactory{TContext}"/>，内部自建无状态的 store/command 实例（二者每次调用
/// 均创建独立 DbContext），避免 Singleton 捕获 Scoped 服务。
/// </para>
/// </summary>
public sealed class WorkspaceTaskAdminService : IWorkspaceTaskAdminService
{
    private const int DefaultEventsLimit = 20;

    private readonly SqliteWorkspaceTaskStore _store;
    private readonly TaskCommandService _commands;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public WorkspaceTaskAdminService(IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _store = new SqliteWorkspaceTaskStore(dbFactory);
        _commands = new TaskCommandService(_store, dbFactory);
    }

    // ── 创建 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAdminGetResult> CreateTaskAsync(TaskAdminCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        var priority = TaskWireMaps.PriorityFromString(request.Priority ?? "p3");
        var executionWindow = TaskWireMaps.ExecutionWindowFromString(request.ExecutionWindow ?? "inherit");
        var origin = TaskWireMaps.OriginFromString(request.Origin);

        var task = await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = request.WorkspaceId,
            Title = request.Title,
            Description = request.Description,
            AcceptanceCriteria = request.AcceptanceCriteria,
            Priority = priority,
            ExecutionWindow = executionWindow,
            PreferredAgentId = request.PreferredAgentId,
            NotBeforeUtc = request.NotBeforeUtc,
            DueAtUtc = request.DueAtUtc,
            SortOrder = request.SortOrder ?? 0,
            Origin = origin,
        }, ct);

        await BackfillActorAsync(request.WorkspaceId, task.TaskId, request.ActorId, setCreatedBy: true, ct);

        var created = await _store.GetTaskAsync(request.WorkspaceId, task.TaskId, ct) ?? task;
        return await BuildGetResultAsync(created, DefaultEventsLimit, ct);
    }

    // ── 列表 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAdminListResult> ListTasksAsync(TaskAdminListQuery query, CancellationToken ct = default)
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

        // keyset 多取一条判 next_cursor。
        var storeQuery = new TaskQuery
        {
            WorkspaceId = query.WorkspaceId,
            Status = status,
            AgentId = query.AgentId,
            Priority = priority,
            Cursor = query.Cursor,
            Limit = query.Limit + 1,
        };

        var tasks = boardStatuses is not null
            ? await _store.QueryTasksAsync(storeQuery, boardStatuses, ct)
            : await _store.QueryTasksAsync(storeQuery, ct);

        var all = tasks.ToList();
        var hasMore = all.Count > query.Limit;
        var page = all.Take(query.Limit).ToList();

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = $"{last.SortOrder.ToString(CultureInfo.InvariantCulture)}|{last.TaskId}";
        }

        return new TaskAdminListResult
        {
            Total = page.Count,
            NextCursor = nextCursor,
            Items = page.Select(ToListItem).ToList(),
        };
    }

    // ── 详情 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAdminGetResult?> GetTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var task = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (task is null)
        {
            return null;
        }

        return await BuildGetResultAsync(task, DefaultEventsLimit, ct);
    }

    // ── 更新 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAdminGetResult> UpdateTaskAsync(TaskAdminUpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);

        var current = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{request.TaskId}' not found.",
                request.TaskId,
                request.ExpectedVersion,
                null);

        var expectedVersion = request.ExpectedVersion ?? current.Version;
        var priority = request.Priority is null ? (TaskPriority?)null : TaskWireMaps.PriorityFromString(request.Priority);
        var executionWindow = request.ExecutionWindow is null ? (TaskExecutionWindow?)null : TaskWireMaps.ExecutionWindowFromString(request.ExecutionWindow);
        var targetStatus = request.Status is null ? (WorkspaceTaskStatus?)null : TaskWireMaps.StatusFromString(request.Status);

        await _commands.PatchAsync(
            request.WorkspaceId,
            request.TaskId,
            expectedVersion,
            request.Title,
            request.Description,
            request.AcceptanceCriteria,
            priority,
            executionWindow,
            request.PreferredAgentId,
            request.NotBeforeUtc,
            request.DueAtUtc,
            request.SortOrder,
            status: targetStatus,
            updatedBy: null,
            ct);

        await BackfillActorAsync(request.WorkspaceId, request.TaskId, request.ActorId, setCreatedBy: false, ct);

        var updated = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct) ?? current;
        return await BuildGetResultAsync(updated, DefaultEventsLimit, ct);
    }

    // ── 删除 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> DeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return await _store.HardDeleteTaskAsync(workspaceId, taskId, ct);
    }

    // ── 命令 ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TaskAdminGetResult> ApplyCommandAsync(TaskAdminCommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);

        var command = ParseCommand(request.Command);

        var current = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{request.TaskId}' not found.",
                request.TaskId,
                request.ExpectedVersion,
                null);

        var expectedVersion = request.ExpectedVersion ?? current.Version;

        await _commands.ApplyCommandAsync(
            request.WorkspaceId,
            request.TaskId,
            command,
            expectedVersion,
            request.AgentId,
            request.WindowDecision,
            request.Reason,
            updatedBy: null,
            ct);

        await BackfillActorAsync(request.WorkspaceId, request.TaskId, request.ActorId, setCreatedBy: false, ct);

        var updated = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct) ?? current;
        return await BuildGetResultAsync(updated, DefaultEventsLimit, ct);
    }

    // ── 映射与帮助 ──────────────────────────────────────────

    private static TaskCommand ParseCommand(string? command) => command switch
    {
        "assign" => TaskCommand.Assign,
        "run_now" => TaskCommand.RunNow,
        "cancel" => TaskCommand.Cancel,
        "reopen" => TaskCommand.Reopen,
        "archive" => TaskCommand.Archive,
        "mark_failed" => TaskCommand.MarkFailed,
        "resume" => TaskCommand.Resume,
        "requeue" => TaskCommand.Requeue,
        _ => throw new TaskStoreException(
            TaskErrorCode.TaskInvalidTransition,
            $"Unknown command wire value '{command}'."),
    };

    private async Task BackfillActorAsync(
        string workspaceId,
        string taskId,
        string? actorId,
        bool setCreatedBy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.TaskId == taskId, ct);
        if (entity is null)
        {
            return;
        }

        if (setCreatedBy)
        {
            entity.CreatedBy = actorId;
        }

        entity.UpdatedBy = actorId;
        await db.SaveChangesAsync(ct);
    }

    private async Task<TaskAdminGetResult> BuildGetResultAsync(WorkspaceTask task, int eventsLimit, CancellationToken ct)
    {
        var allowedTransitions = TaskStateMachine.GetAllowedTransitions(task.Status)
            .Select(TaskWireMaps.StatusToString)
            .ToList();

        var allowedDispositions = Enum.GetValues<TaskDisposition>()
            .Where(d => TaskStateMachine.TryInterpretDisposition(task.Status, d, out _))
            .Select(DispositionToString)
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignment = await BuildAssignmentSummaryAsync(db, task, ct);

        var events = await db.TaskEvents
            .AsNoTracking()
            .Where(e => e.TaskId == task.TaskId)
            .OrderByDescending(e => e.Sequence)
            .Take(Math.Clamp(eventsLimit, 1, 100))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return new TaskAdminGetResult
        {
            Task = ToTaskDetail(task),
            AllowedTransitions = allowedTransitions,
            AllowedDispositions = allowedDispositions,
            ActiveAssignment = assignment,
            RecentEvents = events.Select(ToEventSummary).ToList(),
        };
    }

    private static async Task<TaskAgentAssignmentSummary?> BuildAssignmentSummaryAsync(
        PlatformDbContext db,
        WorkspaceTask task,
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

    private static TaskAdminListItem ToListItem(WorkspaceTask t)
        => new()
        {
            TaskId = t.TaskId,
            Title = t.Title,
            Status = TaskWireMaps.StatusToString(t.Status),
            BoardColumn = SafeProjectBoardColumn(t.Status),
            Priority = TaskWireMaps.PriorityToString(t.Priority),
            ExecutionWindow = TaskWireMaps.ExecutionWindowToString(t.ExecutionWindow),
            PreferredAgentId = t.PreferredAgentId,
            ActiveAssignmentId = t.ActiveAssignmentId,
            ProgressPercent = t.ProgressPercent,
            DueAtUtc = t.DueAtUtc,
            UpdatedAtUtc = t.UpdatedAtUtc,
            Version = t.Version,
        };

    private static TaskAgentTaskDetail ToTaskDetail(WorkspaceTask t)
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
}
