namespace PuddingPlatform.Controllers.External.V1;

/// <summary>External v1 workspace projection; excludes members, access policy and user profile.</summary>
public sealed record ExternalWorkspaceDto
{
    public required string WorkspaceId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsFrozen { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Safe Agent directory projection; prompts, tool grants and main session ID are intentionally omitted.</summary>
public sealed record ExternalAgentDto
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Role { get; init; }
    public string? PreferredProviderId { get; init; }
    public string? PreferredModelId { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsFrozen { get; init; }
    public IReadOnlyList<string> CapabilityIds { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ExternalSendAgentMessageRequest
{
    // Nullable on the wire so the controller can return the stable ExternalErrorResponse
    // instead of letting ApiController emit framework ProblemDetails for a missing property.
    public string? Content { get; init; }
}

public sealed record ExternalMessageDeliveryDto
{
    public required string DeliveryId { get; init; }
    public required string Status { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
}

/// <summary>
/// Asynchronous message receipt. deliveryStatus describes Message Fabric handoff;
/// executionStatus/reply describe the canonical Agent Turn when it exists.
/// </summary>
public sealed record ExternalAgentMessageReceiptDto
{
    public required string MessageId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string DeliveryStatus { get; init; }
    public string? ExecutionStatus { get; init; }
    public string? ConversationId { get; init; }
    public string? Reply { get; init; }
    public string? ReplySummary { get; init; }
    public bool? ReplyIsError { get; init; }
    public IReadOnlyList<ExternalMessageDeliveryDto> Deliveries { get; init; } = [];
    public DateTimeOffset AcceptedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public required string StatusUrl { get; init; }
}
