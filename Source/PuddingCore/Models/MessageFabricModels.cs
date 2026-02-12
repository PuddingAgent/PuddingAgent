namespace PuddingCode.Models;

/// <summary>Endpoint kinds supported by the message fabric.</summary>
public static class MessageEndpointKinds
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Room = "room";
    public const string Connector = "connector";
    public const string System = "system";
}

/// <summary>Message audience modes.</summary>
public static class MessageAudiences
{
    public const string Direct = "direct";
    public const string Room = "room";
    public const string Broadcast = "broadcast";
}

/// <summary>Content type constants for message envelopes.</summary>
public static class MessageContentTypes
{
    public const string Text = "text";
    /// <summary>System heartbeat / proactive check-in messages.</summary>
    public const string Heartbeat = "heartbeat";
}

/// <summary>Visibility modes for room transcripts and deliveries.</summary>
public static class MessageVisibilities
{
    public const string Public = "public";
    public const string Private = "private";
    public const string System = "system";
}

/// <summary>Durable delivery states for endpoint inboxes.</summary>
public static class MessageDeliveryStatuses
{
    public const string Queued = "queued";
    public const string Delivering = "delivering";
    public const string Retrying = "retrying";
    public const string Delivered = "delivered";
    public const string DeadLetter = "dead_letter";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

/// <summary>Address of a message endpoint such as a user, agent, room, connector, or system actor.</summary>
public sealed record MessageAddress
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public string? WorkspaceId { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>A first-class participant in a room; users and agents share this model.</summary>
public sealed record RoomParticipant
{
    public required string ParticipantId { get; init; }
    public required string RoomId { get; init; }
    public required string Kind { get; init; }
    public required string EndpointId { get; init; }
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public bool CanSend { get; init; } = true;
    public bool CanReceive { get; init; } = true;
    public string Status { get; init; } = "available";
}

/// <summary>Bidirectional message envelope submitted by any message client.</summary>
public sealed record MessageEnvelope
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public required MessageAddress From { get; init; }
    public required IReadOnlyList<MessageAddress> To { get; init; }
    public string? RoomId { get; init; }
    public string? ConversationId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public required string Audience { get; init; }
    public required string Visibility { get; init; }
    public string ContentType { get; init; } = "text";
    public required string Content { get; init; }
    public int Priority { get; init; }
    public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Room transcript draft produced by routing a message.</summary>
public sealed record RoomMessageDraft
{
    public required string RoomId { get; init; }
    public required string MessageId { get; init; }
    public required MessageAddress From { get; init; }
    public required string Audience { get; init; }
    public required string Visibility { get; init; }
    public required string Content { get; init; }
    public required long CreatedAt { get; init; }
}

/// <summary>Per-target delivery draft produced by routing a message.</summary>
public sealed record MessageDeliveryDraft
{
    public required string DeliveryId { get; init; }
    public required string MessageId { get; init; }
    public required MessageAddress Target { get; init; }
    public int Priority { get; init; }
}

/// <summary>Routing output containing one room message and zero or more endpoint deliveries.</summary>
public sealed record MessageRoutePlan
{
    public required string MessageId { get; init; }
    public required RoomMessageDraft RoomMessage { get; init; }
    public required IReadOnlyList<MessageDeliveryDraft> Deliveries { get; init; }
}

/// <summary>Result returned after a message has been accepted by the message system.</summary>
public sealed record MessageSendResult
{
    public required string MessageId { get; init; }
    public required string? RoomId { get; init; }
    public required IReadOnlyList<string> DeliveryIds { get; init; }
}

/// <summary>Pull query for an endpoint inbox.</summary>
public sealed record MessageInboxQuery
{
    public required MessageAddress Endpoint { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public int Limit { get; init; } = 20;
    public bool IncludeDelivered { get; init; }
}

/// <summary>Atomic claim request for a durable endpoint delivery.</summary>
public sealed record MessageClaimRequest
{
    public required MessageAddress Endpoint { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public required string ExecutionId { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Inbox projection over a durable message delivery.</summary>
public sealed record MessageInboxItem
{
    public required string DeliveryId { get; init; }
    public required string MessageId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public required MessageAddress From { get; init; }
    public required MessageAddress Target { get; init; }
    public required string Content { get; init; }
    public required string Status { get; init; }
    public int Priority { get; init; }
    public int AttemptCount { get; init; }
    public long CreatedAt { get; init; }
    public long? AvailableAt { get; init; }
    public long? LeaseUntil { get; init; }
    public long? ReadAt { get; init; }
    public long? AckAt { get; init; }
    public string? ClaimedByExecutionId { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Payload carried by message.deliver events in the internal event pipeline.</summary>
public sealed record MessageDeliverEventPayload
{
    public required string MessageId { get; init; }
    public required string DeliveryId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string? RoomId { get; init; }
    public required MessageAddress From { get; init; }
    public required MessageAddress Target { get; init; }
    public required string Content { get; init; }
    public string? ReplyToMessageId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Payload carried by agent.availability.changed events.</summary>
public sealed record AgentAvailabilityChangedEventPayload
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string Status { get; init; }
    public string? RoomId { get; init; }
    public string? CurrentExecutionId { get; init; }
    public string? CurrentTask { get; init; }
}
