namespace PuddingCode.Platform;

/// <summary>会话类型。</summary>
public enum SessionType
{
    ServiceSession,
    TaskSession,
    AuditSession
}

/// <summary>会话状态。</summary>
public enum SessionStatus
{
    Active,
    Idle,
    Completed,
    Failed,
    Frozen
}

/// <summary>ServiceSession 记录。</summary>
public sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string ChannelId { get; init; }
    public required string OwnerUserId { get; init; }
    public SessionType SessionType { get; init; } = SessionType.ServiceSession;
    public SessionStatus Status { get; init; } = SessionStatus.Active;
    public string? RuntimeNodeId { get; init; }
    public string? AgentInstanceId { get; init; }
    /// <summary>会话标题（用户第一条消息截取前30字自动生成，可为 null）。</summary>
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; init; } = DateTimeOffset.UtcNow;
}
