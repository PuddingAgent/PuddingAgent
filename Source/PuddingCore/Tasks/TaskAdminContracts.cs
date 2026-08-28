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

    /// <summary>读取任意任务详情（无 mine 限制）。不存在返回 null（工具层转 task.not_found）。</summary>
    Task<TaskAdminGetResult?> GetTaskAsync(string workspaceId, string taskId, CancellationToken ct = default);

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

    /// <summary>操作者（写入 UpdatedBy）。</summary>
    public string? ActorId { get; init; }
}
