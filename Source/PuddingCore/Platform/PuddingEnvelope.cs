namespace PuddingCode.Platform;

/// <summary>统一入站消息信封，由 Gateway Adapter 产生。</summary>
public sealed record PuddingIngressEnvelope
{
    public string EnvelopeId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>产生该消息的连接器实例，例如 feishu:{agentId}。</summary>
    public string? ConnectorId { get; init; }
    /// <summary>连接器绑定的 Workspace。V1 飞书连接器必须填写。</summary>
    public string? WorkspaceId { get; init; }
    /// <summary>连接器绑定的 Agent。V1 飞书连接器必须填写。</summary>
    public string? AgentId { get; init; }
    public required string ChannelId { get; init; }
    public required string ChannelType { get; init; }
    public required string UserExternalId { get; init; }
    public required string MessageText { get; init; }
    public string? MessageType { get; init; }
    /// <summary>第三方聊天会话标识，例如飞书 chat_id。</summary>
    public string? ExternalConversationId { get; init; }
    /// <summary>第三方消息标识，用于入站幂等和默认回复定位。</summary>
    public string? ExternalMessageId { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>统一出站回复信封，由 Controller 产生、Adapter 回写。</summary>
public sealed record PuddingEgressEnvelope
{
    public string EnvelopeId { get; init; } = Guid.NewGuid().ToString("N");
    public required string ChannelId { get; init; }
    public required string SessionId { get; init; }
    public required string ReplyText { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}
