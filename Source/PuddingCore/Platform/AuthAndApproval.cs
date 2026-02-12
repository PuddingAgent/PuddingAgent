namespace PuddingCode.Platform;

/// <summary>渠道用户上下文——由 ChannelIdentityResolver 解析。</summary>
public sealed record ChannelUserContext
{
    public required string ChannelId { get; init; }
    public required string UserExternalId { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public bool IsResolved { get; init; }
    public string? ResolutionFailureReason { get; init; }
}

/// <summary>权限快照——会话级权限交集结果。</summary>
public sealed record PermissionSnapshot
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public bool IsAllowed { get; init; }
    public string? DenialReason { get; init; }
    public IReadOnlyList<string> EffectiveRoles { get; init; } = [];
}

/// <summary>授权决策。</summary>
public sealed record AuthorizationDecision
{
    public bool IsAllowed { get; init; }
    public string? DenialReason { get; init; }
    public PermissionSnapshot? Snapshot { get; init; }
}

/// <summary>审批状态。</summary>
public enum ApprovalStatus
{
    Pending,
    Confirmed,
    Rejected,
    Expired
}

/// <summary>审批记录。</summary>
public sealed record ApprovalRecord
{
    public string ApprovalId { get; init; } = Guid.NewGuid().ToString("N");
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ActionDescription { get; init; }
    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;
    public string? ConfirmationCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
}
