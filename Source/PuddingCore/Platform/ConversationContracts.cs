namespace PuddingCode.Platform;

// ── 聚合状态枚举 ──────────────────────────────────────────

/// <summary>Conversation 长期生命周期。</summary>
public enum ConversationStatus
{
    Open,
    Archived,
    Deleted,
}

/// <summary>
/// Turn 执行状态。每个 Turn 恰好一个终态。
/// </summary>
public enum TurnStatus
{
    Accepted,
    Running,
    WaitingForTool,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Command 调度状态。
/// </summary>
public enum CommandStatus
{
    Pending,
    Leased,
    Running,
    CancelRequested,  // ADR-059: Cancel API sets this; Worker detects and cancels runCts
    Succeeded,
    Failed,
    Cancelled,
    LeaseLost,
}

/// <summary>
/// ExecutionRun 状态。
/// </summary>
public enum RunStatus
{
    Leased,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    LeaseLost,
}

/// <summary>
/// Turn 终态分类。
/// </summary>
public enum TurnTerminalKind
{
    Completed,
    Failed,
    Cancelled,
}

// ── 错误码 ────────────────────────────────────────────────

public static class TerminalErrorCodes
{
    public const string AgentConfigurationInvalid = "agent_configuration_invalid";
    public const string RuntimeStartRejected = "runtime_start_rejected";
    public const string RuntimeExecutionFailed = "runtime_execution_failed";
    public const string ToolExecutionFailed = "tool_execution_failed";
    public const string ExecutionProtocolError = "execution_protocol_error";
    public const string ExecutionCancelled = "execution_cancelled";
    public const string ExecutionTimeout = "execution_timeout";
    public const string LeaseLost = "lease_lost";
    public const string EventCommitFailed = "event_commit_failed";
}

// ── 事件类型常量 ──────────────────────────────────────────

public static class ConversationEventTypes
{
    public const string TurnAccepted = "turn.accepted";
    public const string TurnStarted = "turn.started";
    public const string TurnWaitingForTool = "turn.waiting_for_tool";
    public const string TurnCompleted = "turn.completed";
    public const string TurnFailed = "turn.failed";
    public const string TurnCancelled = "turn.cancelled";
    public const string TurnCancelRequested = "turn.cancel.requested";

    /// <summary>ADR-058: User message persisted as formal fact</summary>
    public const string MessageCreated = "message.created";

    public const string MessageStarted = "message.started";
    public const string MessageContentAppended = "message.content.appended";
    public const string MessageThinkingSummaryAppended = "message.thinking_summary.appended";
    public const string MessageCompleted = "message.completed";
    public const string MessageFailed = "message.failed";

    public const string ToolCallRequested = "tool.call.requested";
    public const string ToolCallCompleted = "tool.call.completed";
    public const string ToolCallFailed = "tool.call.failed";

    public const string UsageRecorded = "usage.recorded";
    public const string RunLeaseLost = "run.lease_lost";
    public const string ErrorRecorded = "error.recorded";

    public const string ContextCompactionStarted = "context.compaction.started";
    public const string ContextCompactionCompleted = "context.compaction.completed";
    public const string ContextCompactionFailed = "context.compaction.failed";

    public const string ConversationArchived = "conversation.archived";
}
