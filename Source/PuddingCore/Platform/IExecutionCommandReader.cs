namespace PuddingCode.Platform;

/// <summary>
/// Execution command query port.
/// Command writes belong to IConversationAcceptanceStore, lease transitions to
/// IExecutionLeaseStore, and terminal transitions to IExecutionJournal.
/// </summary>
public interface IExecutionCommandReader
{
    Task<ExecutionCommandRecord?> GetAsync(
        string commandId,
        CancellationToken ct = default);

    Task<ExecutionCommandRecord?> FindByTurnIdAsync(
        string conversationId,
        string turnId,
        CancellationToken ct = default);
}

/// <summary>
/// Stable execution references required by the application and Runtime boundary.
/// It deliberately excludes LLM, Tool, Skill and mutable lease configuration.
/// </summary>
public sealed record ExecutionCommandRecord
{
    public required string CommandId { get; init; }
    public required string? TraceId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ConversationId { get; init; }
    public required string AssistantMessageId { get; init; }
    public required string UserMessageId { get; init; }
    public required string TurnId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? UserId { get; init; }
    public string? ChannelId { get; init; }
    public required CommandStatus Status { get; init; }
    public string? RunId { get; init; }
    public ExecutionWorkUnitContext? WorkUnit { get; init; }
}

/// <summary>
/// Canonical Task WorkUnit identity and guardrails resolved from persisted
/// Goal/Task/Plan facts. Command metadata may select the node, but never defines
/// its budget.
/// </summary>
public sealed record ExecutionWorkUnitContext
{
    public required string TaskId { get; init; }
    public required string GoalRunId { get; init; }
    public required string PlanId { get; init; }
    public required string PlanFingerprint { get; init; }
    public required string TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public required string WorkUnitKind { get; init; }
    public required string Objective { get; init; }
    public required int MaxRounds { get; init; }
    public required int MaxToolCallsTotal { get; init; }
    public required int MaxDurationSeconds { get; init; }
    public required long MaxInputTokens { get; init; }
    public required long MaxOutputTokens { get; init; }
    public required decimal MaxCost { get; init; }
}
