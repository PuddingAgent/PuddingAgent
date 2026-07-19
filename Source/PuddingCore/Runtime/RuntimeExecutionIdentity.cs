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
    public string? MessageId { get; init; }
    public string? ToolCallId { get; init; }
    public string? ParentRunId { get; init; }
    public string? InvocationId { get; init; }
    public string? BatchId { get; init; }
    public string? OriginToolId { get; init; }
    public string? Role { get; init; }
}
