namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §9.2 / 设计方案 §12.5: Goal 事件目录一次性冻结。
/// G1 只发射 lifecycle 子集（created/edited/activated/paused/resumed/cancelled/cleared/completed）；
/// iteration / verification / continuation / progress / circuit 常量随 G2/G3 落地启用，
/// 但目录与命名自本文件起不再变更。事件统一通过 ConversationEventStore 追加，
/// SourceKind = Goal，CorrelationId = goalRunId。
/// </summary>
public static class GoalEventTypes
{
    // ── Goal lifecycle（G1）──────────────────────────────────
    public const string Created = "goal.created";
    public const string Edited = "goal.edited";
    public const string Activated = "goal.activated";
    public const string Paused = "goal.paused";
    public const string Resumed = "goal.resumed";
    public const string Cancelled = "goal.cancelled";
    public const string Cleared = "goal.cleared";
    public const string Completed = "goal.completed";
    public const string Blocked = "goal.blocked";
    public const string BudgetExhausted = "goal.budget_exhausted";
    public const string Failed = "goal.failed";

    // ── Iteration（G2）───────────────────────────────────────
    public const string IterationAccepted = "goal.iteration.accepted";
    public const string IterationStarted = "goal.iteration.started";
    public const string IterationSettled = "goal.iteration.settled";

    // ── Verification（G3）────────────────────────────────────
    public const string VerificationRequested = "goal.verification.requested";
    public const string VerificationCompleted = "goal.verification.completed";
    public const string VerificationFailed = "goal.verification.failed";

    // ── Continuation（G2）────────────────────────────────────
    public const string ContinuationRequested = "goal.continuation.requested";
    public const string ContinuationDispatched = "goal.continuation.dispatched";
    public const string ContinuationSuppressed = "goal.continuation.suppressed";

    // ── 进度与熔断（G3）──────────────────────────────────────
    public const string ProgressRecorded = "goal.progress.recorded";
    public const string CircuitOpened = "goal.circuit_opened";

    // ── Task-bound Goal（§22，后续批次）──────────────────────
    public const string TaskGoalBound = "task.goal.bound";
    public const string TaskGoalUnbound = "task.goal.unbound";
    public const string TaskGoalCompleted = "task.goal.completed";
    public const string TaskGoalBlocked = "task.goal.blocked";

    /// <summary>全部 goal.* 事件前缀，供投影 ReadByTypePrefixBackwardAsync 使用。</summary>
    public const string TypePrefix = "goal.";
}

/// <summary>Goal 事件 ProducerComponent 常量（ADR-074 §12.5 envelope 规则）。</summary>
public static class GoalProducerComponents
{
    public const string Command = "goal.command";
    public const string Continuation = "goal.continuation";
    public const string Verifier = "goal.verifier";
    public const string Coordinator = "goal.coordinator";
}
