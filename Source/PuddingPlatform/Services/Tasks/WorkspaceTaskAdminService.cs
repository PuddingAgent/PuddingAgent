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

        // Stage 2（D1/D5）：挂父校验先于创建——失败不留孤儿卡；
        // 错误码由 TaskHierarchyRules 决定（task.parent_not_found / task.hierarchy_invalid），不抛裸异常。
        await ValidateParentOrThrowAsync(request.WorkspaceId, taskId: null, request.ParentTaskId, ct);

        var task = await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = request.WorkspaceId,
            Title = request.Title,
            Description = request.Description,
            AcceptanceCriteria = request.AcceptanceCriteria,
            Priority = priority,
            ExecutionWindow = executionWindow,
            PreferredAgentId = request.PreferredAgentId,
            TaskType = request.TaskType ?? "general",
            RequiredCapabilityIds = request.RequiredCapabilityIds ?? [],
            RequiredProviderId = request.RequiredProviderId,
            RequiredModelId = request.RequiredModelId,
            AllowAgentFallback = request.AllowAgentFallback,
            AutoDispatchEnabled = request.AutoDispatchEnabled,
            NotBeforeUtc = request.NotBeforeUtc,
            DueAtUtc = request.DueAtUtc,
            SortOrder = request.SortOrder ?? 0,
            Origin = origin,
        }, ct);

        await BackfillActorAsync(request.WorkspaceId, task.TaskId, request.ActorId, setCreatedBy: true, ct);

        if (!string.IsNullOrEmpty(request.ParentTaskId))
        {
            // 父子关系走独立持久化原语（只写 parent_task_id + version+1 + task.updated 事件），
            // 绝不经 UpdateTaskAsync 旁路，也绝不触碰 Status / BoardColumn（D3）。
            await _store.SetParentTaskIdAsync(
                task.TaskId,
                request.ParentTaskId,
                task.Version,
                request.ActorId,
                ct);
        }

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

        // Stage 2（D2/D5）：children_of —— 按母卡过滤。子卡集合由专用只读查询取得，
        // 其余过滤（status / board_column / priority）在同层内存中叠加；
        // 说明：children_of 与 agent_id 不叠加（结构视图 vs 归属视图）。
        if (!string.IsNullOrEmpty(query.ParentTaskId))
        {
            var children = await _store.ListChildrenAsync(query.WorkspaceId, query.ParentTaskId, ct);
            all = children
                .Where(t => status is null || t.Status == status.Value)
                .Where(t => boardStatuses is null || boardStatuses.Contains(t.Status))
                .Where(t => priority is null || t.Priority == priority.Value)
                .Where(t => string.IsNullOrEmpty(query.Cursor)
                    || t.SortOrder > ParseCursorSortOrder(query.Cursor)
                    || (t.SortOrder == ParseCursorSortOrder(query.Cursor)
                        && string.CompareOrdinal(t.TaskId, ParseCursorTaskId(query.Cursor)) > 0))
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TaskId, StringComparer.Ordinal)
                .ToList();
        }

        var hasMore = all.Count > query.Limit;
        var page = all.Take(query.Limit).ToList();

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = $"{last.SortOrder.ToString(CultureInfo.InvariantCulture)}|{last.TaskId}";
        }

        // Stage 2（D3 只读聚合投影）：列表页的子卡计数一次批量查询完成（避免 N+1）。
        var childrenByParent = await LoadChildrenByParentAsync(query.WorkspaceId, page, ct);

        return new TaskAdminListResult
        {
            Total = page.Count,
            NextCursor = nextCursor,
            Items = page
                .Select(t => ToListItem(
                    t,
                    childrenByParent.TryGetValue(t.TaskId, out var children) ? children : null,
                    includeChildSummary: query.IncludeChildSummary))
                .ToList(),
        };
    }

    /// <summary>批量装载列表页各卡的子卡（Step 2 只读投影；不写回任何状态，D3）。</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<WorkspaceTask>>> LoadChildrenByParentAsync(
        string workspaceId,
        IReadOnlyList<WorkspaceTask> page,
        CancellationToken ct)
    {
        if (page.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<WorkspaceTask>>(StringComparer.Ordinal);
        }

        var children = await _store.ListChildrenAsync(
            workspaceId,
            page.Select(t => t.TaskId).ToList(),
            ct);

        return children
            .GroupBy(child => child.ParentTaskId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WorkspaceTask>)group.ToList().AsReadOnly(),
                StringComparer.Ordinal);
    }

    private static long ParseCursorSortOrder(string cursor)
    {
        var separator = cursor.IndexOf('|');
        return separator <= 0
            ? long.MinValue
            : long.Parse(cursor[..separator], CultureInfo.InvariantCulture);
    }

    private static string ParseCursorTaskId(string cursor)
    {
        var separator = cursor.IndexOf('|');
        return separator < 0 ? string.Empty : cursor[(separator + 1)..];
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

        // Stage 2（D1/D5）：父关系是独立写路径，三者语义必须泾渭分明：
        // 不传 parent_task_id = 不变更；parent_task_id = 设置/改挂；clear_parent = 显式置 null（脱挂）。
        if (request.ClearParent && !string.IsNullOrEmpty(request.ParentTaskId))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskHierarchyInvalid,
                "clear_parent and parent_task_id are mutually exclusive.",
                request.TaskId,
                expectedVersion,
                current.Version);
        }

        if (!request.ClearParent)
        {
            await ValidateParentOrThrowAsync(request.WorkspaceId, request.TaskId, request.ParentTaskId, ct);
        }

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
            ct,
            taskType: request.TaskType,
            requiredCapabilityIds: request.RequiredCapabilityIds,
            requiredProviderId: request.RequiredProviderId,
            requiredModelId: request.RequiredModelId,
            allowAgentFallback: request.AllowAgentFallback,
            autoDispatchEnabled: request.AutoDispatchEnabled);

        // Stage 2：在字段更新之后、重新读取结果之前写父关系（已校验），
        // 以 patched.Version 作为 CAS；本写入只改 parent_task_id（D3：不碰 Status）。
        if (request.ClearParent || !string.IsNullOrEmpty(request.ParentTaskId))
        {
            var patched = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct) ?? current;
            await _store.SetParentTaskIdAsync(
                request.TaskId,
                request.ClearParent ? null : request.ParentTaskId,
                patched.Version,
                request.ActorId,
                ct);
        }

        await BackfillActorAsync(request.WorkspaceId, request.TaskId, request.ActorId, setCreatedBy: false, ct);

        var updated = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct) ?? current;
        return await BuildGetResultAsync(updated, DefaultEventsLimit, ct);
    }

    // ── 删除 ────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Stage 2（D4）：有子卡的母卡一律 fail-closed 拒结硬删——
    /// 存在未终态子卡 → <see cref="TaskErrorCode.TaskHasNonTerminalChildren"/>（409）；
    /// 仅有终态子卡 → 返回 false（工具层转 task.cannot_hard_delete）。
    /// 硬删母卡会让子卡的 parent_task_id 悬空，破坏层级事实，故本条<b>不提供 force 旁路</b>：
    /// force/级联语义只到 archive 与 cancel。
    /// </remarks>
    public async Task<bool> DeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var children = await _store.ListChildrenAsync(workspaceId, taskId, ct);
        if (children.Count > 0)
        {
            var rejection = TaskHierarchyRules.ValidateArchiveOrCancel(taskId, children);
            if (rejection is not null)
            {
                throw new TaskStoreException(
                    rejection.Value,
                    $"Task '{taskId}' still has non-terminal children and cannot be hard deleted.",
                    taskId);
            }

            return false;
        }

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
            ct: ct,
            force: request.Force);

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

        // Stage 2（D3 只读聚合投影）：容器判定 + 子卡计数。只读：不写回、不派生母卡 Status。
        var children = await _store.ListChildrenAsync(task.WorkspaceId, task.TaskId, ct);

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
            Task = ToTaskDetail(task, children),
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

    private static TaskAdminListItem ToListItem(
        WorkspaceTask t,
        IReadOnlyList<WorkspaceTask>? children = null,
        bool includeChildSummary = false)
    {
        // Stage 2（D2/D3）：IsContainer 由子卡存在性直接判定（只读）；
        // 计数摘要仅在显式请求时输出（保持既有 wire 向后兼容），且<strong>不</strong>回写任何状态。
        var counts = TaskHierarchyRules.CountChildren(t.TaskId, children);
        return new TaskAdminListItem
        {
            TaskId = t.TaskId,
            Title = t.Title,
            Status = TaskWireMaps.StatusToString(t.Status),
            BoardColumn = SafeProjectBoardColumn(t.Status),
            Priority = TaskWireMaps.PriorityToString(t.Priority),
            ExecutionWindow = TaskWireMaps.ExecutionWindowToString(t.ExecutionWindow),
            PreferredAgentId = t.PreferredAgentId,
            TaskType = t.TaskType,
            RequiredCapabilityIds = t.RequiredCapabilityIds,
            RequiredProviderId = t.RequiredProviderId,
            RequiredModelId = t.RequiredModelId,
            AllowAgentFallback = t.AllowAgentFallback,
            AutoDispatchEnabled = t.AutoDispatchEnabled,
            ActiveAssignmentId = t.ActiveAssignmentId,
            ProgressPercent = t.ProgressPercent,
            DueAtUtc = t.DueAtUtc,
            UpdatedAtUtc = t.UpdatedAtUtc,
            Version = t.Version,
            ParentTaskId = t.ParentTaskId,
            IsContainer = counts.Total > 0,
            ChildCount = includeChildSummary ? counts.Total : null,
            CompletedChildCount = includeChildSummary ? counts.Terminal : null,
        };
    }

    private static TaskAgentTaskDetail ToTaskDetail(WorkspaceTask t, IReadOnlyList<WorkspaceTask>? children = null)
    {
        var counts = TaskHierarchyRules.CountChildren(t.TaskId, children);
        return new TaskAgentTaskDetail
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
            RequiredCapabilityIds = t.RequiredCapabilityIds,
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
            ParentTaskId = t.ParentTaskId,
            IsContainer = counts.Total > 0,
            ChildTaskCount = counts.Total,
            CompletedChildCount = counts.Terminal,
        };
    }

    // ── Stage 2：父子层级校验与只读聚合（D1/D3/D5）────────────

    /// <summary>
    /// 挂父校验（单层，D1）：复用 <see cref="TaskHierarchyRules"/>，<b>不另造一套规则</b>。
    /// 失败抛 <see cref="TaskStoreException"/>（结构化错误码，由工具层转 wire code），
    /// 绝不让裸 ArgumentException 逃到调用方。
    /// </summary>
    private async Task ValidateParentOrThrowAsync(
        string workspaceId,
        string? taskId,
        string? parentTaskId,
        CancellationToken ct)
    {
        var rejection = await ValidateParentAsync(workspaceId, taskId, parentTaskId, ct);
        if (rejection is null)
        {
            return;
        }

        throw new TaskStoreException(
            rejection.Value,
            rejection.Value == TaskErrorCode.TaskParentNotFound
                ? $"Parent task '{parentTaskId}' not found in workspace '{workspaceId}'."
                : $"Parent '{parentTaskId}' violates the single-level hierarchy rule (self reference or parent already has a parent).",
            taskId,
            expectedVersion: null,
            actualVersion: null);
    }

    /// <summary>
    /// 返回 null = 合法；否则返回契约错误码。
    /// <paramref name="taskId"/> 为 null（create 尚未生成子卡 ID）时只校验「父存在 + 单层」，
    /// 与 <see cref="TaskHierarchyRules.ValidateParentAssignment"/> 同一口径。
    /// </summary>
    private async Task<TaskErrorCode?> ValidateParentAsync(
        string workspaceId,
        string? taskId,
        string? parentTaskId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parentTaskId))
        {
            return null;
        }

        var parent = await _store.GetTaskAsync(workspaceId, parentTaskId, ct);
        if (string.IsNullOrEmpty(taskId))
        {
            if (parent is null)
            {
                return TaskErrorCode.TaskParentNotFound;
            }

            return string.IsNullOrEmpty(parent.ParentTaskId) ? null : TaskErrorCode.TaskHierarchyInvalid;
        }

        var candidates = new List<WorkspaceTask>(2);
        var self = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (self is not null)
        {
            candidates.Add(self);
        }

        if (parent is not null)
        {
            candidates.Add(parent);
        }

        return TaskHierarchyRules.ValidateParentAssignment(taskId, parentTaskId, candidates);
    }

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
