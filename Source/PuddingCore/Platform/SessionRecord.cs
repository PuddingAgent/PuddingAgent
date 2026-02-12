using System.Text.Json.Serialization;

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

/// <summary>会话在产品信息架构中的角色。</summary>
public enum SessionRole
{
    Main,
    Task,
    Branch,
    Audit
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
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionRole SessionRole { get; init; } = SessionRole.Task;
    public SessionStatus Status { get; init; } = SessionStatus.Active;
    public string? RuntimeNodeId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? RootSessionId { get; init; }
    public string? PrincipalKind { get; init; }
    public string? PrincipalId { get; init; }
    /// <summary>会话标题（用户第一条消息截取前30字自动生成，可为 null）。</summary>
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; init; } = DateTimeOffset.UtcNow;
}
