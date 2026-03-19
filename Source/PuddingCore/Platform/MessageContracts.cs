namespace PuddingCode.Platform;

/// <summary>
/// LLM 配置快照——由 Platform 在入口点解析 DB 配置后随请求下发，
/// Controller/Runtime 无需直接查询 DB 或依赖静态 .env。
/// </summary>
public sealed record LlmConfig
{
    /// <summary>API 基础地址（含 /v1，如 https://api.deepseek.com/v1）</summary>
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
}

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
    /// <summary>由 Platform 解析 Agent 配置后注入；Controller/Runtime 优先使用此值。</summary>
    public LlmConfig? LlmConfig { get; init; }
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
    /// <summary>由 Platform 解析后下发的 LLM 配置；Runtime 转发给 Controller LLM 代理。</summary>
    public LlmConfig? LlmConfig { get; init; }
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
