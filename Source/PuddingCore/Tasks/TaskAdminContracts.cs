namespace PuddingCode.Tasks;

/// <summary>
/// TB-09: 管理者视角任务看板（manage_tasks）跨层命令/查询契约。
/// <para>
/// 与 TB-06 执行者视角 <see cref="ITaskAgentCommandService"/> 互补：本接口无 mine 范围限制，
/// 面向跨 Agent 的完整 CRUD（create/list/get/update/delete）与命令操作（assign/run_now/cancel/
/// reopen/archive/mark_failed/resume/requeue）。wire 字符串由 Platform 侧通过
/// <c>TaskWireMaps</c> 完成枚举 ↔ wire 转换，Runtime 工具只透传/序列化 wire 值，不依赖
/// Platform 类型。
/// </para>
/// </summary>
public interface IWorkspaceTaskAdminService
{
    /// <summary>创建任务（写入看板 Backlog）。</summary>
    Task<TaskAdminGetResult> CreateTaskAsync(TaskAdminCreateRequest request, CancellationToken ct = default);

    /// <summary>跨 Agent 的看板任务列表（五列/状态/优先级/指定 agent 过滤，keyset 分页）。</summary>
    Task<TaskAdminListResult> ListTasksAsync(TaskAdminListQuery query, CancellationToken ct = default);

    /// <summary>读取任意任务详情（无 mine 限制）。不存在返回 null（工具层转 task.not_found）。
    /// includeChildren=true 时结果内联直接子卡列表（children，复用详情路径已查出的查询），默认省略。</summary>
    Task<TaskAdminGetResult?> GetTaskAsync(string workspaceId, string taskId, bool includeChildren = false, CancellationToken ct = default);

    /// <summary>更新任务元数据 + 显式状态迁移（Status 走 CanTransition 校验）。</summary>
    Task<TaskAdminGetResult> UpdateTaskAsync(TaskAdminUpdateRequest request, CancellationToken ct = default);

    /// <summary>硬删除任务（仅无历史 Backlog，返回 false 时工具层转 task.cannot_hard_delete）。</summary>
    Task<bool> DeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default);

    /// <summary>命令操作（assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue）。</summary>
    Task<TaskAdminGetResult> ApplyCommandAsync(TaskAdminCommandRequest request, CancellationToken ct = default);
}

// ── create ─────────────────────────────────────────────────

public sealed record TaskAdminCreateRequest
{
    public required string WorkspaceId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }

    /// <summary>wire 优先级（p0/p1/p2/p3），默认 p3。</summary>
    public string? Priority { get; init; }

    /// <summary>wire 执行窗口（inherit/anytime/off_peak_only），默认 inherit。</summary>
    public string? ExecutionWindow { get; init; }

    public string? PreferredAgentId { get; init; }
    public string? TaskType { get; init; }
    public IReadOnlyList<string>? RequiredCapabilityIds { get; init; }
    public string? RequiredProviderId { get; init; }
    public string? RequiredModelId { get; init; }
    public bool AllowAgentFallback { get; init; }
    public bool AutoDispatchEnabled { get; init; }
    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public long? SortOrder { get; init; }

    /// <summary>wire 任务来源（task.manual/task.auto/automation.schedule），默认 task.manual。</summary>
    public string? Origin { get; init; }

    /// <summary>
    /// Stage 2（D1/D5）：可选父任务 ID（母卡）。
    /// 仅管理者（manage_tasks）可写；服务层用 <see cref="TaskHierarchyRules.ValidateParentAssignment"/>
    /// 校验（父存在、非自身、单层），失败返回 task.parent_not_found / task.hierarchy_invalid，不抛异常。
    /// 挂父<b>不会</b>改动任何 Status / BoardColumn（D3）。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// 看板卡依赖（finish_to_start）：本卡为后继，数组元素为前置任务 ID。
    /// 落库复用 <see cref="ITaskDependencyStore.AddAsync"/>（幂等 + 环检测）；前置不存在/自引用/成环
    /// fail-closed 抛 <see cref="TaskStoreException"/>（task.dependency_task_not_found /
    /// task.dependency_invalid），不静默忽略。与任务内部 WorkUnit 的 DependsOn（TaskExecutionPlanContracts）无关。
    /// </summary>
    public IReadOnlyList<string>? DependsOnTaskIds { get; init; }

    /// <summary>操作者（写入 CreatedBy/UpdatedBy）。</summary>
    public string? ActorId { get; init; }
}

// ── list ──────────────────────────────────────────────────

public sealed record TaskAdminListQuery
{
    public required string WorkspaceId { get; init; }

    /// <summary>wire Status 过滤（Backlog/Ready/.../Archived），与 BoardColumn 互斥。</summary>
    public string? Status { get; init; }

    /// <summary>wire BoardColumn 过滤（Backlog/Todo/InProgress/Done/Failed），与 Status 互斥。</summary>
    public string? BoardColumn { get; init; }

    /// <summary>指定 agent 过滤（跨 agent 视图）。</summary>
    public string? AgentId { get; init; }

    /// <summary>wire Priority 过滤（p0/p1/p2/p3）。</summary>
    public string? Priority { get; init; }

    public int Limit { get; init; } = 50;

    /// <summary>keyset 游标（<c>{sortOrder}|{taskId}</c>）。</summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Stage 2（D2/D5）：children_of —— 只列出 <paramref name="ParentTaskId"/>（母卡）的直接子卡。
    /// 非 null 时按父过滤；仍受 status / board_column / priority 过滤与 keyset 分页约束。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 2（D3）：是否附带<b>只读</b>子卡聚合摘要（IsContainer / ChildCount / CompletedChildCount）。
    /// 默认 false（保持既有 wire 向后兼容）；附带时只是读投影，绝<strong>不</strong>回写母卡 Status。
    /// </summary>
    public bool IncludeChildSummary { get; init; }
}

public sealed record TaskAdminListItem
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }

    /// <summary>Cancelled/Archived 历史筛选时为 null（不占五列）。</summary>
    public string? BoardColumn { get; init; }

    public required string Priority { get; init; }
    public required string ExecutionWindow { get; init; }
    public string? PreferredAgentId { get; init; }
    public string TaskType { get; init; } = "general";
    public IReadOnlyList<string> RequiredCapabilityIds { get; init; } = [];
    public string? RequiredProviderId { get; init; }
    public string? RequiredModelId { get; init; }
    public bool AllowAgentFallback { get; init; }
    public bool AutoDispatchEnabled { get; init; }
    public string? ActiveAssignmentId { get; init; }
    public int? ProgressPercent { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required int Version { get; init; }

    /// <summary>Stage 2（D5）：父任务 ID（只读）；顶层任务为 null。</summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 2（D2）：是否为容器（存在直接子卡）。只读投影：容器不可 claim / 不可自动派发 /
    /// 不可 goal_start，但母卡 Status <b>不</b>因子卡派生（D3）。
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>Stage 2（D3 只读聚合）：直接子卡总数；未请求摘要时为 null。</summary>
    public int? ChildCount { get; init; }

    /// <summary>Stage 2（D3 只读聚合）：终态子卡数（Completed/Failed/Cancelled/Archived）；未请求摘要时为 null。</summary>
    public int? CompletedChildCount { get; init; }
}

public sealed record TaskAdminListResult
{
    public required int Total { get; init; }
    public string? NextCursor { get; init; }
    public required IReadOnlyList<TaskAdminListItem> Items { get; init; }
}

// ── get（详情复用执行者视角的 DTO 结构）────────────────────

public sealed record TaskAdminGetResult
{
    public required TaskAgentTaskDetail Task { get; init; }
    public required IReadOnlyList<string> AllowedTransitions { get; init; }
    public required IReadOnlyList<string> AllowedDispositions { get; init; }
    public TaskAgentAssignmentSummary? ActiveAssignment { get; init; }
    public required IReadOnlyList<TaskAgentEventSummary> RecentEvents { get; init; }

    /// <summary>看板卡依赖投影（前置 + 后继 + 整体评估状态）；未请求依赖读投影时为 null（wire 省略）。</summary>
    public TaskAdminDependencyInfo? Dependencies { get; init; }

    /// <summary>服务端生成的多行缩进依赖树文本（含状态标注；无依赖 → "(no dependencies)"；遇环 → "(cycle detected)"，不抛异常）。</summary>
    public string? DependencyTree { get; init; }

    /// <summary>includeChildren=true 时内联的直接子卡列表（只读，复用单次 ListChildrenAsync 查询）；默认 null（wire 省略）。</summary>
    public IReadOnlyList<TaskAdminChildCard>? Children { get; init; }
}

// ── dependencies（看板卡依赖投影；与 TaskExecutionPlanContracts.DependsOn 的 WorkUnit 依赖无关）──

/// <summary>单条依赖边的对端任务投影（title/status 为对端卡当前 wire 值；对端已被硬删时为 null）。</summary>
public sealed record TaskAdminDependencyEdge
{
    public required string TaskId { get; init; }
    public string? Title { get; init; }
    public string? Status { get; init; }

    /// <summary>satisfied / waiting / broken（与 <see cref="TaskDependencyEvaluationState"/> 同源小写 wire 值）。</summary>
    public required string EvaluationState { get; init; }
}

/// <summary>看板卡依赖投影：前置依赖整体评估状态 + 前置/后继列表。</summary>
public sealed record TaskAdminDependencyInfo
{
    /// <summary>satisfied / waiting / broken（取自 <see cref="ITaskDependencyStore.EvaluateAsync"/>）。</summary>
    public required string State { get; init; }

    /// <summary>与 State 同源的稳定原因码（dependencies_satisfied / waiting_dependency / dependency_terminal_without_completion）。</summary>
    public required string ReasonCode { get; init; }

    public IReadOnlyList<TaskAdminDependencyEdge> Predecessors { get; init; } = [];
    public IReadOnlyList<TaskAdminDependencyEdge> Successors { get; init; } = [];
}

/// <summary>get 内联的直接子卡只读摘要（复用详情路径已查出的 ListChildrenAsync 结果，不新增查询）。</summary>
public sealed record TaskAdminChildCard
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public string? BoardColumn { get; init; }
    public required string Priority { get; init; }
    public required int Version { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

// ── update ────────────────────────────────────────────────

public sealed record TaskAdminUpdateRequest
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }

    /// <summary>CAS 版本；缺省时服务层读取当前 Version。</summary>
    public int? ExpectedVersion { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public string? Priority { get; init; }
    public string? ExecutionWindow { get; init; }
    public string? PreferredAgentId { get; init; }
    public string? TaskType { get; init; }
    public IReadOnlyList<string>? RequiredCapabilityIds { get; init; }
    public string? RequiredProviderId { get; init; }
    public string? RequiredModelId { get; init; }
    public bool? AllowAgentFallback { get; init; }
    public bool? AutoDispatchEnabled { get; init; }

    /// <summary>显式状态迁移（wire Status）。</summary>
    public string? Status { get; init; }

    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public long? SortOrder { get; init; }

    /// <summary>
    /// Stage 2（D1/D5）：设置父任务 ID。
    /// 与 <see cref="ClearParent"/> 互斥；两者都不传 == 不变更父关系（与「传空串」语义区分开）。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 2：显式清除父关系（置 null，脱挂为顶层任务）。
    /// 「不传参数 = 不变更」 vs 「clear_parent = true = 显式清空」必须泾渭分明。
    /// </summary>
    public bool ClearParent { get; init; }

    /// <summary>
    /// 看板卡依赖（finish_to_start）：本卡为后继，数组元素为前置任务 ID。
    /// 语义为「追加」：逐条复用 <see cref="ITaskDependencyStore.AddAsync"/>（幂等，重复传同边不报错）；
    /// 自引用/成环/前置不存在 fail-closed 抛 <see cref="TaskStoreException"/>，不静默忽略。
    /// 不提供移除语义（移除依赖走 TaskSchedulingController 的 DELETE 端点）。
    /// </summary>
    public IReadOnlyList<string>? DependsOnTaskIds { get; init; }

    /// <summary>操作者（写入 UpdatedBy）。</summary>
    public string? ActorId { get; init; }
}

// ── command ───────────────────────────────────────────────

public sealed record TaskAdminCommandRequest
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }

    /// <summary>wire 命令（assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue）。</summary>
    public required string Command { get; init; }

    public int? ExpectedVersion { get; init; }

    /// <summary>assign/run_now 必填。</summary>
    public string? AgentId { get; init; }

    /// <summary>run_now 可选窗口决策。</summary>
    public string? WindowDecision { get; init; }

    /// <summary>cancel/reopen/archive/mark_failed/resume/requeue 可选原因。</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Stage 2（D4）：archive/cancel 在存在未终态子卡时默认 fail-closed 拒绝
    /// （task.has_non_terminal_children）；<see cref="Force"/> = true 时显式级联——
    /// 先把未终态子卡走 cancel，再归档/取消母卡。级联<b>禁止硬删除</b>。
    /// </summary>
    public bool Force { get; init; }

    /// <summary>操作者（写入 UpdatedBy）。</summary>
    public string? ActorId { get; init; }
}
