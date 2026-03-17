namespace PuddingCode.Platform;

/// <summary>消息入口请求。</summary>
public sealed record MessageIngressRequest
{
    public required string ChannelId { get; init; }
    public required string UserExternalId { get; init; }
    public required string MessageText { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? MessageType { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>消息入口响应。</summary>
public sealed record MessageIngressResponse
{
    public required string MessageId { get; init; }
    public required string SessionId { get; init; }
    public string? RouteDecisionId { get; init; }
    public string? Reply { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Runtime 执行请求——Controller 投递到 Runtime。</summary>
public sealed record RuntimeDispatchRequest
{
    public required string SessionId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string MessageText { get; init; }
    public required string WorkspaceId { get; init; }
    public string? AgentInstanceId { get; init; }
    public PermissionSnapshot? PermissionSnapshot { get; init; }
}

/// <summary>Runtime 执行结果——Runtime 回传 Controller。</summary>
public sealed record RuntimeDispatchResult
{
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? ReplyText { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}
