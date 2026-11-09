namespace PuddingCode.Runtime;

/// <summary>
/// 运行实例类型。身份由编排层创建，Runtime 和 Tool 只能透传，不能从 SessionId 推断。
/// </summary>
public enum RuntimeExecutionKind
{
    ConversationTurn,
    SubAgent,
}

/// <summary>
/// 一次 Runtime 执行的稳定身份。
/// Conversation/Turn/Command 属于父会话事实；RunId 标识当前执行实例。
/// 子代理使用独立 RunId，并通过 ParentRunId 关联父执行。
/// </summary>
public sealed record RuntimeExecutionIdentity
{
    public required RuntimeExecutionKind Kind { get; init; }
    public required string ConversationId { get; init; }
    public string? TurnId { get; init; }
    public string? CommandId { get; init; }
    public required string RunId { get; init; }
    public required string? TraceId { get; init; }
    public string? MessageId { get; init; }
    public string? ToolCallId { get; init; }
    public string? ParentRunId { get; init; }
    public string? InvocationId { get; init; }
    public string? BatchId { get; init; }
    public string? OriginToolId { get; init; }
    public string? Role { get; init; }

    /// <summary>
    /// Host-bound budget identity of a managed canonical Work. Only the orchestration layer that has cross-checked
    /// canonical acceptance, command/lease and account binding may set it; Runtime and tools only forward it and must
    /// never synthesize, copy from a parent run or infer it from session ids.
    /// </summary>
    public RuntimeWorkBudgetBinding? WorkBudget { get; init; }
}

/// <summary>
/// Immutable, non-secret budget carrier for one managed Work run. It intentionally has no attempt id: each model send
/// derives its own attempt identity from this binding.
/// </summary>
public sealed record RuntimeWorkBudgetBinding(
    string AccountId,
    IReadOnlyList<string> Ancestors,
    string RootKind,
    string RootId,
    string WorkspaceId,
    string AgentId,
    string SessionId,
    string CostUnit,
    string ModelRevision,
    string PriceVersion,
    long MaxInputTokensPerAttempt,
    long MaxOutputTokensPerAttempt)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(AccountId) && AccountId.Length <= 128
        && Ancestors is not null
        && !string.IsNullOrWhiteSpace(RootKind)
        && RootKind is "Work" or "GoalRun" or "Task"
        && !string.IsNullOrWhiteSpace(RootId) && RootId.Length <= 128
        && !string.IsNullOrWhiteSpace(WorkspaceId) && WorkspaceId.Length <= 128
        && !string.IsNullOrWhiteSpace(AgentId) && AgentId.Length <= 128
        && !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 128
        && !string.IsNullOrWhiteSpace(CostUnit) && CostUnit.Length <= 32
        && !string.IsNullOrWhiteSpace(ModelRevision) && ModelRevision.Length <= 128
        && !string.IsNullOrWhiteSpace(PriceVersion) && PriceVersion.Length <= 128
        && MaxInputTokensPerAttempt >= 0 && MaxOutputTokensPerAttempt >= 0;
}
