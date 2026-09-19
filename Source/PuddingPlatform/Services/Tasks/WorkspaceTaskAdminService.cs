using System.Globalization;
using System.Text;
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

    /// <summary>依赖树文本前置链展开的最大深度（防御上限；环由渲染期 onPath 检测截断，超深节点按叶行呈现）。</summary>
    private const int DependencyTreeMaxDepth = 8;

    private readonly SqliteWorkspaceTaskStore _store;
    private readonly TaskCommandService _commands;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ITaskDependencyStore _dependencies;

    public WorkspaceTaskAdminService(IDbContextFactory<PlatformDbContext> dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _store = new SqliteWorkspaceTaskStore(dbFactory);
        _commands = new TaskCommandService(_store, dbFactory);
        // 沿用本类「构造仅依赖 IDbContextFactory、内部自建无状态实例」的既有惯例（避免加宽 DI 变更面；
        // TaskDependencyStore 无状态、每次调用自建 DbContext）。
        _dependencies = new TaskDependencyStore(dbFactory, timeProvider ?? TimeProvider.System);
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

        // 看板卡依赖：与挂父同序 fail-closed——先校验全部前置存在（缺前置 → task.dependency_task_not_found），
        // 校验先于建卡，不留孤儿卡；自引用在建卡场景不可能（taskId 尚未生成），成环亦不可能（新卡无出边）。
        if (request.DependsOnTaskIds is { Count: > 0 })
        {
            await ValidateDependencyPredecessorsAsync(request.WorkspaceId, request.DependsOnTaskIds, ct);
        }

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

        if (request.DependsOnTaskIds is { Count: > 0 })
        {
            await AddDependenciesOrThrowAsync(request.WorkspaceId, task.TaskId, request.DependsOnTaskIds, ct);
        }

        var created = await _store.GetTaskAsync(request.WorkspaceId, task.TaskId, ct) ?? task;
        return await BuildGetResultAsync(created, DefaultEventsLimit, ct, includeDependencies: true);
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
    public async Task<TaskAdminGetResult?> GetTaskAsync(string workspaceId, string taskId, bool includeChildren = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var task = await _store.GetTaskAsync(workspaceId, taskId, ct);
        if (task is null)
        {
            return null;
        }

        return await BuildGetResultAsync(task, DefaultEventsLimit, ct, includeChildren: includeChildren, includeDependencies: true);
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

        if (request.DependsOnTaskIds is { Count: > 0 })
        {
            // 看板卡依赖（追加语义，幂等）：自引用/成环/前置缺失由 store 与 AddDependenciesOrThrowAsync
            // fail-closed 转 task.dependency_invalid / task.dependency_task_not_found，不静默忽略。
            await AddDependenciesOrThrowAsync(request.WorkspaceId, request.TaskId, request.DependsOnTaskIds, ct);
        }

        var updated = await _store.GetTaskAsync(request.WorkspaceId, request.TaskId, ct) ?? current;
        return await BuildGetResultAsync(updated, DefaultEventsLimit, ct, includeDependencies: true);
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
        return await BuildGetResultAsync(updated, DefaultEventsLimit, ct, includeDependencies: true);
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

    private async Task<TaskAdminGetResult> BuildGetResultAsync(
        WorkspaceTask task,
        int eventsLimit,
        CancellationToken ct,
        bool includeChildren = false,
        bool includeDependencies = false)
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

        // 看板卡依赖读投影（与父子层级正交：只读，不派生 Status）。
        TaskAdminDependencyInfo? dependencies = null;
        string? dependencyTree = null;
        if (includeDependencies)
        {
            (dependencies, dependencyTree) = await BuildDependencyProjectionAsync(task.WorkspaceId, task.TaskId, ct);
        }

        return new TaskAdminGetResult
        {
            Task = ToTaskDetail(task, children),
            AllowedTransitions = allowedTransitions,
            AllowedDispositions = allowedDispositions,
            ActiveAssignment = assignment,
            RecentEvents = events.Select(ToEventSummary).ToList(),
            Dependencies = dependencies,
            DependencyTree = dependencyTree,
            Children = includeChildren ? children.Select(ToChildCard).ToList() : null,
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

    private static TaskAdminChildCard ToChildCard(WorkspaceTask child) => new()
    {
        TaskId = child.TaskId,
        Title = child.Title,
        Status = TaskWireMaps.StatusToString(child.Status),
        BoardColumn = SafeProjectBoardColumn(child.Status),
        Priority = TaskWireMaps.PriorityToString(child.Priority),
        Version = child.Version,
        UpdatedAtUtc = child.UpdatedAtUtc,
    };

    // ── 看板卡依赖（depends_on 读写投影；与父子层级语义正交，不派生 Status）─────

    /// <summary>
    /// create 路径的前置存在性预校验（先校验后建卡，不留孤儿卡）：任一前置不存在 →
    /// <see cref="TaskErrorCode.TaskDependencyTaskNotFound"/>（task.dependency_task_not_found）。
    /// </summary>
    private async Task ValidateDependencyPredecessorsAsync(
        string workspaceId,
        IReadOnlyList<string> predecessorIds,
        CancellationToken ct)
    {
        var distinct = predecessorIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskDependencyInvalid,
                "depends_on_task_ids must contain at least one non-empty task id.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var found = await db.WorkspaceTasks.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && distinct.Contains(t.TaskId))
            .Select(t => t.TaskId)
            .ToListAsync(ct);
        var missing = distinct.Except(found, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskDependencyTaskNotFound,
                $"Dependency predecessor task '{missing[0]}' not found in workspace '{workspaceId}'.",
                missing[0]);
        }
    }

    /// <summary>
    /// 逐条建立依赖（本卡 = 后继）：复用 <see cref="ITaskDependencyStore.AddAsync"/> 的幂等与环检测；
    /// store 的 InvalidOperationException（消息为 task_dependency_* 码）统一转结构化
    /// <see cref="TaskStoreException"/>，fail-closed，绝不静默忽略。
    /// </summary>
    private async Task AddDependenciesOrThrowAsync(
        string workspaceId,
        string successorTaskId,
        IReadOnlyList<string> predecessorIds,
        CancellationToken ct)
    {
        foreach (var predecessorId in predecessorIds.Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _dependencies.AddAsync(workspaceId, predecessorId, successorTaskId, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("task_dependency_", StringComparison.Ordinal))
            {
                throw ex.Message switch
                {
                    "task_dependency_task_not_found" => new TaskStoreException(
                        TaskErrorCode.TaskDependencyTaskNotFound,
                        $"Dependency predecessor task '{predecessorId}' not found.",
                        predecessorId),
                    "task_dependency_self_reference" => new TaskStoreException(
                        TaskErrorCode.TaskDependencyInvalid,
                        $"Task '{successorTaskId}' cannot depend on itself.",
                        successorTaskId),
                    "task_dependency_cycle" => new TaskStoreException(
                        TaskErrorCode.TaskDependencyInvalid,
                        $"Adding dependency '{predecessorId}' -> '{successorTaskId}' would create a cycle.",
                        successorTaskId),
                    _ => new TaskStoreException(TaskErrorCode.TaskDependencyInvalid, ex.Message, successorTaskId),
                };
            }
        }
    }

    /// <summary>
    /// 单卡依赖读投影：边 = <see cref="ITaskDependencyStore.ListAsync"/>（双向）；
    /// 整体评估 = <see cref="ITaskDependencyStore.EvaluateAsync"/>（本卡）；后继每项的贡献状态
    /// 也取自 EvaluateAsync(后继)（本卡是否在其 broken/waiting 集合中），单一事实源，不复制围栏逻辑。
    /// </summary>
    private async Task<(TaskAdminDependencyInfo Info, string Tree)> BuildDependencyProjectionAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct)
    {
        var edges = await _dependencies.ListAsync(workspaceId, taskId, ct);
        var predecessorIds = edges.Where(e => e.SuccessorTaskId == taskId)
            .Select(e => e.PredecessorTaskId).Distinct(StringComparer.Ordinal).ToList();
        var successorIds = edges.Where(e => e.PredecessorTaskId == taskId)
            .Select(e => e.SuccessorTaskId).Distinct(StringComparer.Ordinal).ToList();

        var evaluation = await _dependencies.EvaluateAsync(workspaceId, taskId, ct);

        // 前置链展开（BFS，深度上限 + 全局去重；环在渲染期由 onPath 标注，此处不抛异常）。
        var adjacency = new Dictionary<string, List<TaskDependency>>(StringComparer.Ordinal)
        {
            [taskId] = edges.ToList(),
        };
        var knownIds = new HashSet<string>(StringComparer.Ordinal) { taskId };
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var id in predecessorIds)
        {
            if (knownIds.Add(id))
            {
                queue.Enqueue((id, 1));
            }
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= DependencyTreeMaxDepth || adjacency.ContainsKey(current))
            {
                continue;
            }

            var currentEdges = await _dependencies.ListAsync(workspaceId, current, ct);
            adjacency[current] = currentEdges.ToList();
            foreach (var parentId in currentEdges.Where(e => e.SuccessorTaskId == current)
                         .Select(e => e.PredecessorTaskId).Distinct(StringComparer.Ordinal))
            {
                if (knownIds.Add(parentId))
                {
                    queue.Enqueue((parentId, depth + 1));
                }
            }
        }

        // 涉及任务（链上全部 + 直接后继）的 title/status 一次批量查询。
        var allIds = adjacency.Keys.Concat(successorIds).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cards = await db.WorkspaceTasks.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && allIds.Contains(t.TaskId))
            .Select(t => new { t.TaskId, t.Title, t.Status })
            .ToDictionaryAsync(
                t => t.TaskId,
                t => (Title: (string?)t.Title, t.Status),
                StringComparer.Ordinal);

        var brokenSet = evaluation.BrokenByTaskIds.ToHashSet(StringComparer.Ordinal);
        var waitingSet = evaluation.WaitingOnTaskIds.ToHashSet(StringComparer.Ordinal);

        var predecessors = predecessorIds
            .Select(id => ToDependencyEdge(
                id,
                cards,
                brokenSet.Contains(id) ? "broken" : waitingSet.Contains(id) ? "waiting" : "satisfied"))
            .ToList();

        var successors = new List<TaskAdminDependencyEdge>();
        foreach (var successorId in successorIds)
        {
            string state;
            if (!cards.ContainsKey(successorId))
            {
                // 后继卡已被硬删（防御；正常数据下依赖边随卡硬删不可达）：按 broken 呈现，不抛异常。
                state = "broken";
            }
            else
            {
                var successorEvaluation = await _dependencies.EvaluateAsync(workspaceId, successorId, ct);
                state = successorEvaluation.BrokenByTaskIds.Contains(taskId) ? "broken"
                    : successorEvaluation.WaitingOnTaskIds.Contains(taskId) ? "waiting"
                    : "satisfied";
            }

            successors.Add(ToDependencyEdge(successorId, cards, state));
        }

        var info = new TaskAdminDependencyInfo
        {
            State = evaluation.State.ToString().ToLowerInvariant(),
            ReasonCode = evaluation.ReasonCode,
            Predecessors = predecessors,
            Successors = successors,
        };
        var tree = BuildDependencyTreeText(taskId, cards, adjacency, predecessors, successors);
        return (info, tree);
    }

    private static TaskAdminDependencyEdge ToDependencyEdge(
        string taskId,
        Dictionary<string, (string? Title, WorkspaceTaskStatus Status)> cards,
        string evaluationState)
    {
        if (!cards.TryGetValue(taskId, out var card))
        {
            return new TaskAdminDependencyEdge
            {
                TaskId = taskId,
                Title = null,
                Status = null,
                EvaluationState = evaluationState,
            };
        }

        return new TaskAdminDependencyEdge
        {
            TaskId = taskId,
            Title = card.Title,
            Status = TaskWireMaps.StatusToString(card.Status),
            EvaluationState = evaluationState,
        };
    }

    /// <summary>
    /// 服务端生成的多行缩进依赖树文本（确定性）：无依赖 → "(no dependencies)"；
    /// 前置链递展开（深度上限内），行尾标注卡状态与评估状态；遇环 → "(cycle detected)"，不抛异常。
    /// </summary>
    private static string BuildDependencyTreeText(
        string rootId,
        Dictionary<string, (string? Title, WorkspaceTaskStatus Status)> cards,
        Dictionary<string, List<TaskDependency>> adjacency,
        IReadOnlyList<TaskAdminDependencyEdge> predecessors,
        IReadOnlyList<TaskAdminDependencyEdge> successors)
    {
        if (predecessors.Count == 0 && successors.Count == 0)
        {
            return "(no dependencies)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"self: {rootId} | {TitleOf(cards, rootId)} | {StatusOf(cards, rootId)}");
        sb.AppendLine("predecessors:");
        var onPath = new HashSet<string>(StringComparer.Ordinal) { rootId };
        foreach (var edge in predecessors)
        {
            AppendEdgeLine(sb, edge, "<-", "  ");
            RenderAncestors(sb, edge.TaskId, adjacency, cards, onPath, depth: 2);
        }

        sb.AppendLine("successors:");
        foreach (var edge in successors)
        {
            AppendEdgeLine(sb, edge, "->", "  ");
        }

        return sb.ToString().TrimEnd();
    }

    private static void RenderAncestors(
        StringBuilder sb,
        string id,
        Dictionary<string, List<TaskDependency>> adjacency,
        Dictionary<string, (string? Title, WorkspaceTaskStatus Status)> cards,
        HashSet<string> onPath,
        int depth)
    {
        var parents = adjacency.TryGetValue(id, out var edges)
            ? edges.Where(e => e.SuccessorTaskId == id)
                .Select(e => e.PredecessorTaskId)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];
        var indent = new string(' ', depth * 2);
        foreach (var parentId in parents)
        {
            sb.AppendLine($"{indent}<- {parentId} | {TitleOf(cards, parentId)} | {StatusOf(cards, parentId)}");
            if (onPath.Contains(parentId))
            {
                sb.AppendLine($"{indent}  (cycle detected)");
                continue;
            }

            onPath.Add(parentId);
            RenderAncestors(sb, parentId, adjacency, cards, onPath, depth + 1);
            onPath.Remove(parentId);
        }
    }

    private static void AppendEdgeLine(
        StringBuilder sb,
        TaskAdminDependencyEdge edge,
        string arrow,
        string indent) => sb.AppendLine(
        $"{indent}{arrow} {edge.TaskId} | {edge.Title ?? "-"} | {edge.Status ?? "-"} | {edge.EvaluationState}");

    private static string TitleOf(
        Dictionary<string, (string? Title, WorkspaceTaskStatus Status)> cards,
        string taskId) => cards.TryGetValue(taskId, out var card) ? card.Title ?? "-" : "-";

    private static string StatusOf(
        Dictionary<string, (string? Title, WorkspaceTaskStatus Status)> cards,
        string taskId) => cards.TryGetValue(taskId, out var card) ? TaskWireMaps.StatusToString(card.Status) : "-";

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
