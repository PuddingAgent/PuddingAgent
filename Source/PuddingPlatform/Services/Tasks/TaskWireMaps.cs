using Microsoft.AspNetCore.Http;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-03: 枚举 ↔ wire 字符串双向映射 + 错误码 → wire/HTTP 映射。
/// wire 值以 TB-01 <see cref="WorkspaceTaskStatus"/> / <see cref="TaskPriority"/> /
/// <see cref="TaskExecutionWindow"/> / <see cref="BoardColumn"/> / <see cref="TaskErrorCode"/>
/// 注释为权威。未知 wire 值 fail-closed（抛 <see cref="TaskStoreException"/>，映射为 422）。
/// </summary>
public static class TaskWireMaps
{
    // ── Status ──────────────────────────────────────────────

    public static string StatusToString(WorkspaceTaskStatus status) => status switch
    {
        WorkspaceTaskStatus.Backlog => "Backlog",
        WorkspaceTaskStatus.Ready => "Ready",
        WorkspaceTaskStatus.Deferred => "Deferred",
        WorkspaceTaskStatus.Reserved => "Reserved",
        WorkspaceTaskStatus.Assigned => "Assigned",
        WorkspaceTaskStatus.NeedsReview => "NeedsReview",
        WorkspaceTaskStatus.InProgress => "InProgress",
        WorkspaceTaskStatus.Blocked => "Blocked",
        WorkspaceTaskStatus.Completed => "Completed",
        WorkspaceTaskStatus.Failed => "Failed",
        WorkspaceTaskStatus.Cancelled => "Cancelled",
        WorkspaceTaskStatus.Archived => "Archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知任务状态。"),
    };

    /// <summary>未知/null 值 fail-closed：抛 <see cref="TaskStoreException"/>(<see cref="TaskErrorCode.TaskInvalidTransition"/>)。</summary>
    public static WorkspaceTaskStatus StatusFromString(string? value) => value switch
    {
        "Backlog" => WorkspaceTaskStatus.Backlog,
        "Ready" => WorkspaceTaskStatus.Ready,
        "Deferred" => WorkspaceTaskStatus.Deferred,
        "Reserved" => WorkspaceTaskStatus.Reserved,
        "Assigned" => WorkspaceTaskStatus.Assigned,
        "NeedsReview" => WorkspaceTaskStatus.NeedsReview,
        "InProgress" => WorkspaceTaskStatus.InProgress,
        "Blocked" => WorkspaceTaskStatus.Blocked,
        "Completed" => WorkspaceTaskStatus.Completed,
        "Failed" => WorkspaceTaskStatus.Failed,
        "Cancelled" => WorkspaceTaskStatus.Cancelled,
        "Archived" => WorkspaceTaskStatus.Archived,
        _ => throw InvalidWire("status", value),
    };

    // ── BoardColumn ─────────────────────────────────────────

    public static string BoardColumnToString(BoardColumn column) => column switch
    {
        BoardColumn.Backlog => "Backlog",
        BoardColumn.Todo => "Todo",
        BoardColumn.InProgress => "InProgress",
        BoardColumn.Done => "Done",
        BoardColumn.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "未知看板列。"),
    };

    /// <summary>未知/null 值 fail-closed：抛 <see cref="TaskStoreException"/>(<see cref="TaskErrorCode.TaskInvalidTransition"/>)。</summary>
    public static BoardColumn BoardColumnFromString(string? value) => value switch
    {
        "Backlog" => BoardColumn.Backlog,
        "Todo" => BoardColumn.Todo,
        "InProgress" => BoardColumn.InProgress,
        "Done" => BoardColumn.Done,
        "Failed" => BoardColumn.Failed,
        _ => throw InvalidWire("boardColumn", value),
    };

    /// <summary>boardColumn → 状态集合（权威 = TaskStateMachine.ProjectBoardColumn 反查）。</summary>
    public static IReadOnlyList<WorkspaceTaskStatus> BoardColumnToStatuses(BoardColumn column) => column switch
    {
        BoardColumn.Backlog => new[] { WorkspaceTaskStatus.Backlog },
        BoardColumn.Todo => new[]
        {
            WorkspaceTaskStatus.Ready,
            WorkspaceTaskStatus.Deferred,
            WorkspaceTaskStatus.Reserved,
            WorkspaceTaskStatus.Assigned,
            WorkspaceTaskStatus.NeedsReview
        },
        BoardColumn.InProgress => new[] { WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Blocked },
        BoardColumn.Done => new[] { WorkspaceTaskStatus.Completed },
        BoardColumn.Failed => new[] { WorkspaceTaskStatus.Failed },
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "未知看板列。"),
    };

    // ── Priority ────────────────────────────────────────────

    public static string PriorityToString(TaskPriority priority) => priority switch
    {
        TaskPriority.P0 => "p0",
        TaskPriority.P1 => "p1",
        TaskPriority.P2 => "p2",
        TaskPriority.P3 => "p3",
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "未知任务优先级。"),
    };

    /// <summary>未知/null 值 fail-closed。</summary>
    public static TaskPriority PriorityFromString(string? value) => value switch
    {
        "p0" => TaskPriority.P0,
        "p1" => TaskPriority.P1,
        "p2" => TaskPriority.P2,
        "p3" => TaskPriority.P3,
        _ => throw InvalidWire("priority", value),
    };

    // ── ExecutionWindow ─────────────────────────────────────

    public static string ExecutionWindowToString(TaskExecutionWindow window) => window switch
    {
        TaskExecutionWindow.Inherit => "inherit",
        TaskExecutionWindow.Anytime => "anytime",
        TaskExecutionWindow.OffPeakOnly => "off_peak_only",
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "未知执行窗口。"),
    };

    /// <summary>未知/null 值 fail-closed。</summary>
    public static TaskExecutionWindow ExecutionWindowFromString(string? value) => value switch
    {
        "inherit" => TaskExecutionWindow.Inherit,
        "anytime" => TaskExecutionWindow.Anytime,
        "off_peak_only" => TaskExecutionWindow.OffPeakOnly,
        _ => throw InvalidWire("executionWindow", value),
    };

    // ── Error code ──────────────────────────────────────────

    /// <summary>TaskErrorCode → 稳定 code（wire）。以 TB-01 枚举注释为权威。</summary>
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

    /// <summary>TaskEventType → wire 字符串（task.created / task.ready / ...，以 TB-01 枚举注释为权威）。</summary>
    public static string EventTypeToString(TaskEventType eventType) => eventType switch
    {
        TaskEventType.TaskCreated => "task.created",
        TaskEventType.TaskUpdated => "task.updated",
        TaskEventType.TaskReady => "task.ready",
        TaskEventType.TaskDeferred => "task.deferred",
        TaskEventType.TaskReserved => "task.reserved",
        TaskEventType.TaskAssigned => "task.assigned",
        TaskEventType.TaskAccepted => "task.accepted",
        TaskEventType.TaskProgressed => "task.progressed",
        TaskEventType.TaskBlocked => "task.blocked",
        TaskEventType.TaskAssignmentRejected => "task.assignment_rejected",
        TaskEventType.TaskCompleted => "task.completed",
        TaskEventType.TaskFailed => "task.failed",
        TaskEventType.TaskReopened => "task.reopened",
        TaskEventType.TaskCancelled => "task.cancelled",
        TaskEventType.TaskArchived => "task.archived",
        TaskEventType.TaskDispatchRequested => "task.dispatch.requested",
        TaskEventType.TaskDispatchDeferred => "task.dispatch.deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "未知任务事件类型。"),
    };

    // ── Comment author kind ──────────────────────────────────

    /// <summary>TaskCommentAuthorKind → wire 字符串（user / agent / system）。</summary>
    public static string CommentAuthorKindToString(TaskCommentAuthorKind kind) => kind switch
    {
        TaskCommentAuthorKind.User => "user",
        TaskCommentAuthorKind.Agent => "agent",
        TaskCommentAuthorKind.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知评论作者类型。"),
    };

    /// <summary>wire 字符串 → TaskCommentAuthorKind。null/空/"user" → User；未知值 fail-closed。</summary>
    public static TaskCommentAuthorKind CommentAuthorKindFromString(string? value) => value switch
    {
        null or "" or "user" => TaskCommentAuthorKind.User,
        "agent" => TaskCommentAuthorKind.Agent,
        "system" => TaskCommentAuthorKind.System,
        _ => throw InvalidWire("authorKind", value),
    };

    /// <summary>TaskErrorCode → HTTP 状态（契约 §五）。</summary>
    public static int ErrorCodeToHttpStatus(TaskErrorCode code) => code switch
    {
        TaskErrorCode.TaskNotFound or TaskErrorCode.AssignmentNotFound or TaskErrorCode.AgentNotFound
            => StatusCodes.Status404NotFound,
        TaskErrorCode.TaskVersionConflict or TaskErrorCode.TaskStateConflict or
        TaskErrorCode.AssignmentAlreadyActive or TaskErrorCode.AssignmentStale or
        TaskErrorCode.AgentUnavailable or TaskErrorCode.PolicyVersionConflict
            => StatusCodes.Status409Conflict,
        TaskErrorCode.TaskInvalidTransition or TaskErrorCode.TaskInvalidDisposition or
        TaskErrorCode.TaskReasonRequired or TaskErrorCode.TaskResultRequired or
        TaskErrorCode.TaskArtifactRequired or TaskErrorCode.TaskNotReopenable or
        TaskErrorCode.TaskCannotHardDelete or TaskErrorCode.PolicyInvalid or
        TaskErrorCode.TaskActiveContextMissing or TaskErrorCode.TaskInvalidCursor
            => StatusCodes.Status422UnprocessableEntity,
        TaskErrorCode.CapabilityMissing => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static TaskStoreException InvalidWire(string field, string? value)
        => new(
            TaskErrorCode.TaskInvalidTransition,
            $"Unknown {field} wire value '{value}'.",
            taskId: null,
            expectedVersion: null,
            actualVersion: null);
}
