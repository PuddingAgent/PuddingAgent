namespace PuddingCode.Goals;

/// <summary>goal_outbox.kind/status 的稳定 wire 值。</summary>
public static class GoalOutboxValues
{
    public const string Continuation = "continuation";
    public const string Pending = "pending";
    public const string Leased = "leased";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string DeadLettered = "dead_lettered";
}

/// <summary>
/// 仅供进程内 GoalContinuationWorker 构造的受信上下文。HTTP DTO 不映射此对象；
/// Conversation Acceptance 必须用它在同一事务重验 Goal/epoch/version/outbox lease，
/// 并原子提交 synthetic Turn、Iteration 预算消费和 outbox 完成。
/// </summary>
public sealed record GoalContinuationAcceptanceContext
{
    public required string OutboxId { get; init; }
    public required string GoalRunId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public required int IterationNo { get; init; }
    public required string LeaseOwner { get; init; }
    public required long FencingToken { get; init; }
    public string? TaskId { get; init; }
    public int? ExpectedTaskVersion { get; init; }
    public long? ReservationFencingToken { get; init; }
    public string? TaskPlanId { get; init; }
    public string? TaskPlanFingerprint { get; init; }
    public string? TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
}

/// <summary>Goal synthetic message 使用的受控 metadata 键。</summary>
public static class GoalContinuationMetadata
{
    public const string Managed = "goal_managed";
    public const string Origin = "automation_origin";
    public const string OriginValue = "goal_continuation";
    public const string GoalRunId = "goal_run_id";
    public const string ActivationEpoch = "goal_activation_epoch";
    public const string AggregateVersion = "goal_aggregate_version";
    public const string ObjectiveVersion = "goal_objective_version";
    public const string IterationNo = "goal_iteration_no";
    public const string OutboxId = "goal_outbox_id";
    public const string TaskPlanId = "task_plan_id";
    public const string TaskPlanFingerprint = "task_plan_fingerprint";
    public const string TaskNodeId = "task_node_id";
    public const string ParentTaskNodeId = "parent_task_node_id";
}

/// <summary>受理时可判定的稳定失败码。</summary>
public static class GoalContinuationAcceptanceErrorCodes
{
    public const string GoalMissing = "goal_missing";
    public const string GoalInactive = "goal_inactive";
    public const string StaleEpoch = "stale_epoch";
    public const string StaleVersion = "stale_version";
    public const string StaleLease = "stale_lease";
    public const string BudgetExhausted = "budget_exhausted";
    public const string IterationConflict = "iteration_conflict";
    public const string ConversationBusy = "conversation_busy";
    public const string TaskFenceChanged = "task_fence_changed";
    public const string TaskPlanChanged = "task_plan_changed";
}

/// <summary>
/// Conversation Acceptance 对 Goal continuation 的 fail-closed 裁决。
/// Deferred=true 表示保持同一 durable intent 稍后重试；否则旧意图只能 suppress。
/// </summary>
public sealed class GoalContinuationAcceptanceException(
    string code,
    string message,
    bool deferred = false) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool Deferred { get; } = deferred;
}
