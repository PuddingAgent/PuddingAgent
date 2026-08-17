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

    /// <summary>构造统一错误体 JSON（§7：code/message/可选 task_id/current_version/current_status）。</summary>
    public static string BuildErrorJson(
        TaskErrorCode code,
        string message,
        string? taskId = null,
        int? currentVersion = null,
        string? currentStatus = null)
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
