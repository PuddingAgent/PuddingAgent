namespace PuddingCode.Platform;

/// <summary>路由决策记录——记录消息如何命中 Workspace 与 AgentTemplate。</summary>
public sealed record RouteDecisionRecord
{
    public string RouteDecisionId { get; init; } = Guid.NewGuid().ToString("N");
    public required string MessageId { get; init; }
    public required string ChannelId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? SessionId { get; init; }
    public bool IsSuccess { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>审计事件类型。</summary>
public enum AuditEventType
{
    MessageReceived,
    SessionCreated,
    SessionReused,
    RouteDecision,
    RouteFailure,
    ApprovalRequested,
    ApprovalConfirmed,
    ApprovalRejected,
    RuntimeDispatched,
    RuntimeReplyReceived,
    PermissionDenied,
    WorkspaceFrozen,
    WorkspaceResumed,
    AgentInstanceCreated,
    AgentExecutionCompleted,
    AgentExecutionFailed
}

/// <summary>审计事件记录。</summary>
public sealed record AuditEventRecord
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public required AuditEventType EventType { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? ApprovalId { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
