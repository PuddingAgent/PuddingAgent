namespace PuddingCode.Tasks;

/// <summary>工作区任务状态（WorkspaceTask 生命周期，12 态）。wire 值见各成员注释，序列化映射由 TB-03 API 层处理。</summary>
public enum WorkspaceTaskStatus
{
    /// <summary>待规划（目标/验收/执行信息未完整）。wire: Backlog</summary>
    Backlog,

    /// <summary>待办。wire: Ready</summary>
    Ready,

    /// <summary>待办（被 Fence 推迟，必有 decisionCode + nextEligibleAtUtc）。wire: Deferred</summary>
    Deferred,

    /// <summary>待办（Scheduler 短暂持久所有权）。wire: Reserved</summary>
    Reserved,

    /// <summary>待办（已分配 Agent）。wire: Assigned</summary>
    Assigned,

    /// <summary>待办（需用户复盘）。wire: NeedsReview</summary>
    NeedsReview,

    /// <summary>进行中。wire: InProgress</summary>
    InProgress,

    /// <summary>进行中（阻塞，必有 blockerKind + blockerReason）。wire: Blocked</summary>
    Blocked,

    /// <summary>已完成。wire: Completed</summary>
    Completed,

    /// <summary>已失败（闭合失败）。wire: Failed</summary>
    Failed,

    /// <summary>已取消（历史筛选）。wire: Cancelled</summary>
    Cancelled,

    /// <summary>已归档（历史筛选）。wire: Archived</summary>
    Archived
}

/// <summary>看板五列投影（不是第二套状态机，由 TaskStateMachine 投影）。</summary>
public enum BoardColumn
{
    /// <summary>待规划列。← Backlog</summary>
    Backlog,

    /// <summary>待办列。← Ready | Deferred | Reserved | Assigned | NeedsReview</summary>
    Todo,

    /// <summary>进行中列。← InProgress | Blocked</summary>
    InProgress,

    /// <summary>已完成列。← Completed</summary>
    Done,

    /// <summary>已失败列。← Failed</summary>
    Failed
}

/// <summary>task_update 的 disposition（Agent 不直接写状态，由后端状态机解释）。</summary>
public enum TaskDisposition
{
    /// <summary>accept。wire: accept</summary>
    Accept,

    /// <summary>progress（仅更新进度，状态不变）。wire: progress</summary>
    Progress,

    /// <summary>todo（回退待办）。wire: todo</summary>
    Todo,

    /// <summary>blocked（必填 reason）。wire: blocked</summary>
    Blocked,

    /// <summary>needs_approval → Blocked + blockerKind=approval_required。wire: needs_approval</summary>
    NeedsApproval,

    /// <summary>rejected（必填 reason，终止 Assignment 不终止 Task）。wire: rejected</summary>
    Rejected,

    /// <summary>completed（必填 resultSummary + 必需 Artifact）。wire: completed</summary>
    Completed
}

/// <summary>任务来源。</summary>
public enum TaskOrigin
{
    /// <summary>手工创建。wire: task.manual</summary>
    Manual,

    /// <summary>自动创建。wire: task.auto</summary>
    Auto,

    /// <summary>定时任务创建。wire: automation.schedule</summary>
    AutomationSchedule
}

/// <summary>任务优先级。</summary>
public enum TaskPriority
{
    /// <summary>P0。wire: p0</summary>
    P0,

    /// <summary>P1。wire: p1</summary>
    P1,

    /// <summary>P2。wire: p2</summary>
    P2,

    /// <summary>P3。wire: p3</summary>
    P3
}

/// <summary>任务执行窗口。</summary>
public enum TaskExecutionWindow
{
    /// <summary>继承工作区策略。wire: inherit</summary>
    Inherit,

    /// <summary>任意时间。wire: anytime</summary>
    Anytime,

    /// <summary>仅峰谷低峰。wire: off_peak_only</summary>
    OffPeakOnly
}

/// <summary>WorkAdmissionFence 决策码。</summary>
public enum DecisionCode
{
    /// <summary>wire: allowed_user_direct</summary>
    AllowedUserDirect,

    /// <summary>wire: allowed_off_peak</summary>
    AllowedOffPeak,

    /// <summary>wire: allowed_priority_bypass</summary>
    AllowedPriorityBypass,

    /// <summary>wire: allowed_explicit_override</summary>
    AllowedExplicitOverride,

    /// <summary>wire: deferred_peak_window</summary>
    DeferredPeakWindow,

    /// <summary>wire: deferred_not_before</summary>
    DeferredNotBefore,

    /// <summary>wire: deferred_agent_busy</summary>
    DeferredAgentBusy,

    /// <summary>wire: deferred_agent_offline</summary>
    DeferredAgentOffline,

    /// <summary>wire: deferred_agent_cooldown</summary>
    DeferredAgentCooldown,

    /// <summary>wire: deferred_user_message_pending</summary>
    DeferredUserMessagePending,

    /// <summary>wire: denied_policy_invalid</summary>
    DeniedPolicyInvalid,

    /// <summary>wire: denied_task_state_changed</summary>
    DeniedTaskStateChanged,

    /// <summary>wire: denied_stale_assignment</summary>
    DeniedStaleAssignment,

    /// <summary>wire: denied_workspace_frozen</summary>
    DeniedWorkspaceFrozen,

    /// <summary>wire: denied_agent_frozen</summary>
    DeniedAgentFrozen
}

/// <summary>任务命令（幂等）。</summary>
public enum TaskCommand
{
    /// <summary>新建任务 → Backlog。wire: Create</summary>
    Create,

    /// <summary>更新元数据（非终态保持当前状态）。wire: Update</summary>
    Update,

    /// <summary>分配（Ready → Reserved）。wire: Assign</summary>
    Assign,

    /// <summary>立即执行（Ready/Deferred → Reserved）。wire: RunNow</summary>
    RunNow,

    /// <summary>取消。wire: Cancel</summary>
    Cancel,

    /// <summary>归档（Completed/Cancelled/Failed → Archived）。wire: Archive</summary>
    Archive,

    /// <summary>重开（Failed → Ready 唯一入口，递增 Version，产生 task.reopened）。wire: Reopen</summary>
    Reopen,

    /// <summary>标记失败。wire: MarkFailed</summary>
    MarkFailed,

    /// <summary>恢复（Blocked/NeedsReview → Ready）。wire: Resume</summary>
    Resume,

    /// <summary>重新排队（Deferred/Ready → Ready）。wire: Requeue</summary>
    Requeue
}

/// <summary>任务错误码（稳定 code，随 HTTP 状态返回）。</summary>
public enum TaskErrorCode
{
    /// <summary>task.not_found 404</summary>
    TaskNotFound,

    /// <summary>task.version_conflict 409</summary>
    TaskVersionConflict,

    /// <summary>task.state_conflict 409</summary>
    TaskStateConflict,

    /// <summary>task.invalid_transition 422</summary>
    TaskInvalidTransition,

    /// <summary>task.invalid_disposition 422</summary>
    TaskInvalidDisposition,

    /// <summary>task.reason_required 422</summary>
    TaskReasonRequired,

    /// <summary>task.result_required 422</summary>
    TaskResultRequired,

    /// <summary>task.artifact_required 422</summary>
    TaskArtifactRequired,

    /// <summary>task.not_reopenable 422</summary>
    TaskNotReopenable,

    /// <summary>task.cannot_hard_delete 422</summary>
    TaskCannotHardDelete,

    /// <summary>assignment.not_found 404</summary>
    AssignmentNotFound,

    /// <summary>assignment.already_active 409</summary>
    AssignmentAlreadyActive,

    /// <summary>assignment.stale 409</summary>
    AssignmentStale,

    /// <summary>agent.not_found 404</summary>
    AgentNotFound,

    /// <summary>agent.unavailable 409</summary>
    AgentUnavailable,

    /// <summary>capability.missing 403</summary>
    CapabilityMissing,

    /// <summary>policy.invalid 422</summary>
    PolicyInvalid,

    /// <summary>policy.version_conflict 409</summary>
    PolicyVersionConflict,

    /// <summary>task.active_context_missing 422（claim/update 无 Active Task Context）</summary>
    TaskActiveContextMissing,

    /// <summary>task.invalid_cursor 422（task_list 游标非法）</summary>
    TaskInvalidCursor
}

/// <summary>task.* 事件类型（不含 automation.* / work_policy.*，那是 P1）。</summary>
public enum TaskEventType
{
    /// <summary>task.created</summary>
    TaskCreated,

    /// <summary>task.updated</summary>
    TaskUpdated,

    /// <summary>task.ready</summary>
    TaskReady,

    /// <summary>task.deferred</summary>
    TaskDeferred,

    /// <summary>task.reserved</summary>
    TaskReserved,

    /// <summary>task.assigned</summary>
    TaskAssigned,

    /// <summary>task.accepted</summary>
    TaskAccepted,

    /// <summary>task.progressed</summary>
    TaskProgressed,

    /// <summary>task.blocked</summary>
    TaskBlocked,

    /// <summary>task.assignment_rejected</summary>
    TaskAssignmentRejected,

    /// <summary>task.completed</summary>
    TaskCompleted,

    /// <summary>task.failed</summary>
    TaskFailed,

    /// <summary>task.reopened</summary>
    TaskReopened,

    /// <summary>task.cancelled</summary>
    TaskCancelled,

    /// <summary>task.archived</summary>
    TaskArchived,

    /// <summary>task.dispatch.requested</summary>
    TaskDispatchRequested,

    /// <summary>task.dispatch.deferred</summary>
    TaskDispatchDeferred
}

/// <summary>Assignment Attempt 状态（最小集）。</summary>
public enum AssignmentStatus
{
    /// <summary>已分配，等待 Agent 认领/执行。</summary>
    Assigned,

    /// <summary>Agent 已认领（task_claim + task_update accept）。</summary>
    Accepted,

    /// <summary>完成终态。</summary>
    Completed,

    /// <summary>拒绝终态（固化 reason）。</summary>
    Rejected
}

/// <summary>工作区任务台账（与 TaskPlanRun/TaskNode 严格分离，不复用不继承）。</summary>
public sealed record WorkspaceTask
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>任务标题。</summary>
    public required string Title { get; init; }

    /// <summary>任务描述。</summary>
    public string? Description { get; init; }

    /// <summary>验收标准。</summary>
    public string? AcceptanceCriteria { get; init; }

    /// <summary>任务状态。</summary>
    public WorkspaceTaskStatus Status { get; init; } = WorkspaceTaskStatus.Backlog;

    /// <summary>优先级。</summary>
    public TaskPriority Priority { get; init; } = TaskPriority.P3;

    /// <summary>执行窗口。</summary>
    public TaskExecutionWindow ExecutionWindow { get; init; } = TaskExecutionWindow.Inherit;

    /// <summary>偏好 Agent ID。</summary>
    public string? PreferredAgentId { get; init; }

    /// <summary>当前活跃 Assignment ID。</summary>
    public string? ActiveAssignmentId { get; init; }

    /// <summary>最早可执行时间（UTC）。</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    /// <summary>截止时间（UTC）。</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>下一次可派发时间（UTC）。</summary>
    public DateTimeOffset? NextEligibleAtUtc { get; init; }

    /// <summary>排序序号。</summary>
    public long SortOrder { get; init; }

    /// <summary>进度百分比。</summary>
    public int? ProgressPercent { get; init; }

    /// <summary>进度摘要。</summary>
    public string? ProgressSummary { get; init; }

    /// <summary>阻塞类型（Blocked 时必填）。</summary>
    public string? BlockerKind { get; init; }

    /// <summary>阻塞原因（Blocked 时必填）。</summary>
    public string? BlockerReason { get; init; }

    /// <summary>失败码（Failed 时）。</summary>
    public string? FailureCode { get; init; }

    /// <summary>失败原因（Failed 时）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>乐观并发版本号。</summary>
    public int Version { get; init; } = 1;

    /// <summary>创建者。</summary>
    public string? CreatedBy { get; init; }

    /// <summary>最后更新者。</summary>
    public string? UpdatedBy { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>失败时间（UTC）。</summary>
    public DateTimeOffset? FailedAtUtc { get; init; }

    /// <summary>归档时间（UTC）。</summary>
    public DateTimeOffset? ArchivedAtUtc { get; init; }
}

/// <summary>一次 Assignment Attempt（task_assignment_attempts）。</summary>
public sealed record AssignmentAttempt
{
    /// <summary>Assignment ID。</summary>
    public required string AssignmentId { get; init; }

    /// <summary>归属任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>被分配 Agent ID。</summary>
    public required string AgentId { get; init; }

    /// <summary>Assignment 状态。</summary>
    public AssignmentStatus Status { get; init; } = AssignmentStatus.Assigned;

    /// <summary>乐观并发版本号。</summary>
    public int Version { get; init; } = 1;

    /// <summary>拒绝原因（Rejected 时固化）。</summary>
    public string? RejectionReason { get; init; }

    /// <summary>结果摘要。</summary>
    public string? ResultSummary { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>开始时间（UTC）。</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Delivery ID。</summary>
    public string? DeliveryId { get; init; }

    /// <summary>Execution ID。</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Session ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>Run ID。</summary>
    public string? RunId { get; init; }

    /// <summary>Trace ID。</summary>
    public string? TraceId { get; init; }
}

/// <summary>任务执行绑定（task_execution_bindings）。</summary>
public sealed record TaskExecutionBinding
{
    /// <summary>绑定 ID。</summary>
    public required string BindingId { get; init; }

    /// <summary>归属任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>Assignment ID。</summary>
    public string? AssignmentId { get; init; }

    /// <summary>Delivery ID。</summary>
    public string? DeliveryId { get; init; }

    /// <summary>Execution ID。</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Session ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>Run ID。</summary>
    public string? RunId { get; init; }

    /// <summary>Trace ID。</summary>
    public string? TraceId { get; init; }

    /// <summary>Agent ID。</summary>
    public string? AgentId { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>任务事件（task_events，每 Task Sequence 单调递增）。</summary>
public sealed record TaskEvent
{
    /// <summary>事件 ID。</summary>
    public required string EventId { get; init; }

    /// <summary>归属任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>每 Task 单调递增的序号。</summary>
    public required long Sequence { get; init; }

    /// <summary>事件类型。</summary>
    public required TaskEventType EventType { get; init; }

    /// <summary>Assignment ID。</summary>
    public string? AssignmentId { get; init; }

    /// <summary>Agent ID。</summary>
    public string? AgentId { get; init; }

    /// <summary>Delivery ID。</summary>
    public string? DeliveryId { get; init; }

    /// <summary>Execution ID。</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Session ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>来源。</summary>
    public TaskOrigin? Origin { get; init; }

    /// <summary>优先级。</summary>
    public TaskPriority? Priority { get; init; }

    /// <summary>决策码（Deferred 事件必填）。</summary>
    public string? DecisionCode { get; init; }

    /// <summary>下一次可派发时间（UTC）。</summary>
    public DateTimeOffset? NextEligibleAtUtc { get; init; }

    /// <summary>Trace ID。</summary>
    public string? TraceId { get; init; }

    /// <summary>Correlation ID。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Causation ID。</summary>
    public string? CausationId { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
