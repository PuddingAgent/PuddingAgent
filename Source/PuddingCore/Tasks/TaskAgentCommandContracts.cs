using System.Text.Json;

namespace PuddingCode.Tasks;

/// <summary>
/// TB-06: Agent Task 工具的跨层命令/查询契约。
/// <para>
/// 四工具落在 PuddingRuntime（不引用 PuddingPlatform），故本接口定义在 PuddingCore，
/// 由 PuddingPlatform 的 <c>TaskAgentCommandService</c> 实现。wire 字符串在 Platform 侧
/// 通过 <c>TaskWireMaps</c> 完成枚举 ↔ wire 转换，Runtime 工具只透传/序列化 wire 值，
/// 不依赖 Platform 类型。
/// </para>
/// </summary>
public interface ITaskAgentCommandService
{
    /// <summary>mine 范围任务列表（keyset 分页）。</summary>
    Task<TaskAgentListResult> ListMineAsync(TaskAgentListQuery query, CancellationToken ct = default);

    /// <summary>
    /// 读取单个任务详情（mine 范围）。非 mine 任务与不存在统一返回 null（信息隐藏，
    /// 不暴露其他 Agent 任务存在性——评审裁决 §二.1）。
    /// </summary>
    Task<TaskAgentGetResult?> GetAsync(
        string workspaceId,
        string taskId,
        string agentId,
        int eventsLimit,
        CancellationToken ct = default);

    /// <summary>认领（Assigned→InProgress + Attempt InProgress + task.accepted + binding 回填）。claim 与 update(accept) 共用。</summary>
    Task<TaskAgentMutationResult> ClaimAsync(TaskAgentClaimRequest request, CancellationToken ct = default);

    /// <summary>disposition 解释（复用 TaskStateMachine.TryInterpretDisposition）+ CAS + Attempt 推进 + Event 原子提交。</summary>
    Task<TaskAgentMutationResult> ApplyDispositionAsync(TaskAgentUpdateRequest request, CancellationToken ct = default);
}

// ── task_list ────────────────────────────────────────────────

public sealed record TaskAgentListQuery
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }

    /// <summary>wire Status 过滤（Backlog/Ready/.../Archived）。</summary>
    public string? Status { get; init; }

    /// <summary>wire BoardColumn 过滤（Backlog/Todo/InProgress/Done/Failed）。</summary>
    public string? BoardColumn { get; init; }

    /// <summary>wire Priority 过滤（p0/p1/p2/p3）。</summary>
    public string? Priority { get; init; }

    public int Limit { get; init; } = 50;

    /// <summary>keyset 游标（<c>{sortOrder}|{taskId}</c>）。</summary>
    public string? Cursor { get; init; }
}

public sealed record TaskAgentListItem
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    /// <summary>Cancelled/Archived 历史筛选时为 null（不占五列）。</summary>
    public string? BoardColumn { get; init; }
    public required string Priority { get; init; }
    public required string ExecutionWindow { get; init; }
    public string? ActiveAssignmentId { get; init; }
    public int? ProgressPercent { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required int Version { get; init; }

    /// <summary>
    /// Stage 2（D5）：父任务 ID，<b>只读</b>。执行者侧（task_list/task_get）没有任何修改父子关系的参数，
    /// 父子关系仅管理者（manage_tasks）可写。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>Stage 2（D2，只读）：是否为容器（存在直接子卡）——容器不可被 claim。</summary>
    public bool IsContainer { get; init; }
}

public sealed record TaskAgentListResult
{
    public required int Total { get; init; }
    public string? NextCursor { get; init; }
    public required IReadOnlyList<TaskAgentListItem> Items { get; init; }
}

// ── task_get ────────────────────────────────────────────────

public sealed record TaskAgentGetResult
{
    public required TaskAgentTaskDetail Task { get; init; }
    public required IReadOnlyList<string> AllowedTransitions { get; init; }
    public required IReadOnlyList<string> AllowedDispositions { get; init; }
    public TaskAgentAssignmentSummary? ActiveAssignment { get; init; }
    public required IReadOnlyList<TaskAgentEventSummary> RecentEvents { get; init; }
}

public sealed record TaskAgentTaskDetail
{
    public required string TaskId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public required string Status { get; init; }

    /// <summary>Cancelled/Archived 时为 null（不占五列，避免 ProjectBoardColumn 抛异常）。</summary>
    public string? BoardColumn { get; init; }

    /// <summary>是否已归档/已取消（历史参考标记）。</summary>
    public bool Archived { get; init; }

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
    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
    public required long SortOrder { get; init; }
    public int? ProgressPercent { get; init; }
    public string? ProgressSummary { get; init; }
    public string? BlockerKind { get; init; }
    public string? BlockerReason { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>wire: "task.manual"/"task.auto"/"automation.schedule"。</summary>
    public string? Origin { get; init; }

    public required int Version { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? FailedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }

    /// <summary>
    /// Stage 2（D5）：父任务 ID，<b>只读</b>暴露；执行者侧无任何父子关系写参数（D5）。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 2（D2）：是否为容器（存在直接子卡）。容器不可 claim / 不可自动派发 / 不可 goal_start；
    /// 但母卡 Status 不因子卡派生（D3）。
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>Stage 2（D3 只读聚合投影）：直接子卡总数。</summary>
    public int ChildTaskCount { get; init; }

    /// <summary>Stage 2（D3 只读聚合投影）：终态子卡数；<b>纯展示</b>，不参与任何状态派生。</summary>
    public int CompletedChildCount { get; init; }
}

public sealed record TaskAgentAssignmentSummary
{
    public required string AssignmentId { get; init; }
    public required string AgentId { get; init; }
    public required string Status { get; init; }
    public string? RejectionReason { get; init; }
    public string? DeliveryId { get; init; }
    public string? ExecutionId { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public string? TraceId { get; init; }
}

public sealed record TaskAgentEventSummary
{
    public required string EventId { get; init; }
    public required long Sequence { get; init; }
    public required string EventType { get; init; }
    public string? AssignmentId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

// ── task_claim / task_update ────────────────────────────────

public sealed record TaskAgentClaimRequest
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required string AssignmentId { get; init; }
    public required int ExpectedVersion { get; init; }
    public required string AgentId { get; init; }
    public string? ExecutionId { get; init; }
    public string? SessionId { get; init; }
    public string? TraceId { get; init; }
}

public sealed record TaskAgentUpdateRequest
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required string AssignmentId { get; init; }
    public required int ExpectedVersion { get; init; }
    public required string Disposition { get; init; }
    public required string AgentId { get; init; }
    public string? Reason { get; init; }
    public int? ProgressPercent { get; init; }
    public string? ProgressSummary { get; init; }
    public string? ResultSummary { get; init; }
    public IReadOnlyList<string>? Artifacts { get; init; }
    public string? ExecutionId { get; init; }
    public string? SessionId { get; init; }
    public string? TraceId { get; init; }
}

public sealed record TaskAgentMutationResult
{
    public required string TaskId { get; init; }
    public required string Disposition { get; init; }
    public required string Status { get; init; }
    public required int Version { get; init; }
    public required string AssignmentId { get; init; }
    public required string AssignmentStatus { get; init; }
    public required string Event { get; init; }
    public required string BoardColumn { get; init; }
    public string? BlockerKind { get; init; }
    public string? BlockerReason { get; init; }
    public int? ProgressPercent { get; init; }
    public string? ProgressSummary { get; init; }

    /// <summary>
    /// Stage 3（D5 收口）：父任务 ID，<b>只读</b>投影。执行者侧（task_claim / task_update）
    /// 没有任何修改父子关系的写参数，父子关系仅管理者（manage_tasks）可写。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 3（D2 收口，只读）：是否为容器（存在直接子卡）。容器不可 claim，
    /// 故 claim / update 路径对容器恒被拒；该标记仅作只读展示，不参与任何状态派生（D3）。
    /// </summary>
    public bool IsContainer { get; init; }
}

/// <summary>
/// 工具侧错误体构造（§7 统一错误协议）。Runtime 工具不引用 PuddingPlatform，
/// 故 <see cref="TaskErrorCode"/> → wire 的映射在此提供（与 Platform 的 TaskWireMaps 保持一致）。
/// </summary>
public static class TaskToolErrors
{
    /// <summary>TaskErrorCode → 稳定 wire code（合同冻结 v1 §2.9 + TB-06 增补 2 个）。</summary>
    public static string ErrorCodeToString(TaskErrorCode code) => code switch
    {
        TaskErrorCode.TaskNotFound => "task.not_found",
        TaskErrorCode.TaskVersionConflict => "task.version_conflict",
        TaskErrorCode.TaskStateConflict => "task.state_conflict",
        TaskErrorCode.TaskInvalidTransition => "task.invalid_transition",
        TaskErrorCode.TaskInvalidDisposition => "task.invalid_disposition",
        TaskErrorCode.TaskReasonRequired => "task.reason_required",
        TaskErrorCode.TaskResultRequired => "task.result_required",
        TaskErrorCode.TaskArtifactRequired => "task.artifact_required",
        TaskErrorCode.TaskNotReopenable => "task.not_reopenable",
        TaskErrorCode.TaskCannotHardDelete => "task.cannot_hard_delete",
        // Stage 1/2（D1/D4）：父层级 3 个错误码的 wire 映射。此前只登记了枚举成员与 Platform 侧
        // TaskWireMaps，Runtime 工具侧（本表）漏登记，会落到 `_ => code.ToString()` 输出 PascalCase
        // 枚举名（如 "TaskParentNotFound"），与合同冻结的 task.parent_not_found / task.hierarchy_invalid /
        // task.has_non_terminal_children 不一致——工具层补齐闭环。
        TaskErrorCode.TaskParentNotFound => "task.parent_not_found",
        TaskErrorCode.TaskHierarchyInvalid => "task.hierarchy_invalid",
        TaskErrorCode.TaskHasNonTerminalChildren => "task.has_non_terminal_children",
        TaskErrorCode.TaskDependencyTaskNotFound => "task.dependency_task_not_found",
        TaskErrorCode.TaskDependencyInvalid => "task.dependency_invalid",
        TaskErrorCode.AssignmentNotFound => "assignment.not_found",
        TaskErrorCode.AssignmentAlreadyActive => "assignment.already_active",
        TaskErrorCode.AssignmentStale => "assignment.stale",
        TaskErrorCode.AgentNotFound => "agent.not_found",
        TaskErrorCode.AgentUnavailable => "agent.unavailable",
        TaskErrorCode.CapabilityMissing => "capability.missing",
        TaskErrorCode.PolicyInvalid => "policy.invalid",
        TaskErrorCode.PolicyVersionConflict => "policy.version_conflict",
        TaskErrorCode.TaskActiveContextMissing => "task.active_context_missing",
        TaskErrorCode.TaskInvalidCursor => "task.invalid_cursor",
        _ => code.ToString(),
    };

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>构造统一错误体 JSON（§7：code/message/可选 task_id/current_version/current_status/context_rebuild）。</summary>
    public static string BuildErrorJson(
        TaskErrorCode code,
        string message,
        string? taskId = null,
        int? currentVersion = null,
        string? currentStatus = null,
        TaskContextRebuildDiagnostics? contextRebuild = null)
    {
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code = ErrorCodeToString(code),
                message,
                task_id = taskId,
                current_version = currentVersion,
                current_status = currentStatus,
                context_rebuild = contextRebuild,
            },
        }, ErrorJsonOptions);
    }

    /// <summary>由 <see cref="TaskStoreException"/> 构造统一错误体 JSON。</summary>
    public static string BuildErrorJson(TaskStoreException ex)
        => BuildErrorJson(
            ex.ErrorCode,
            ex.Message,
            ex.TaskId,
            ex.ActualVersion,
            currentStatus: null);
}

/// <summary>
/// Active Task Context 缺失时「反查归属重建」失败原因的非泄露诊断（卡 3133b149）。
/// <para>
/// 只暴露阶段与结论，不区分「任务不存在」与「任务归属其他 Agent」，也不携带归属方
/// Agent/任务标题/所有者摘要，故不破坏 Platform 的 mine 信息隐藏裁决。
/// 缺失该对象 = 调用方未走反查重建（例如注入上下文自身参数不匹配的真实调用错误）。
/// </para>
/// </summary>
public sealed record TaskContextRebuildDiagnostics
{
    /// <summary>是否真的执行过反查（入参不完整时为 false）。</summary>
    public required bool Attempted { get; init; }

    /// <summary>失败阶段：inputs / lookup / ownership（重建成功不产生错误体）。</summary>
    public required string Stage { get; init; }

    /// <summary>结论：incomplete_inputs / not_visible / agent_mismatch。</summary>
    public required string Outcome { get; init; }
}
