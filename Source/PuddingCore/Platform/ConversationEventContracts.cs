using System.Text.Json;

namespace PuddingCode.Platform;

/// <summary>
/// ADR-057: 统一 Conversation 事件 Envelope。
/// sequence 是 Envelope 一等字段，不得注入 Payload JSON。
/// </summary>
public sealed record ConversationEvent
{
    public required string EventId { get; init; }
    public required string ConversationId { get; init; }
    public required long Sequence { get; init; }

    public required string WorkspaceId { get; init; }
    public required string TurnId { get; init; }
    public string? CommandId { get; init; }
    public string? RunId { get; init; }
    public string? MessageId { get; init; }

    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }

    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public string? ProducerEventId { get; init; }

    public required JsonElement Payload { get; init; }
}

/// <summary>
/// 待持久化的新事件草稿（不含 sequence，由 Event Store 分配）。
/// 必须携带完整 Envelope：conversation、workspace、turn、command、run。
/// </summary>
public sealed record NewConversationEvent(
    string EventId,
    string Type,
    int SchemaVersion,
    string? WorkspaceId,
    string? TurnId,
    string? CommandId,
    string? RunId,
    string? MessageId,
    string? CorrelationId,
    string? CausationId,
    string? ProducerEventId,
    JsonElement Payload
);

/// <summary>
/// Event Store append 结果。
/// </summary>
public sealed record AppendResult(
    long FirstSequence,
    long LastSequence,
    int Count
);

/// <summary>
/// 事件分页。
/// </summary>
public sealed record EventPage(
    IReadOnlyList<ConversationEvent> Events,
    long? NextCursor,
    bool HasMore
);

/// <summary>
/// Conversation 事件边界。
/// </summary>
public sealed record EventBounds(
    long? MinSequence,
    long? MaxSequence
);

/// <summary>
/// 事件写入条件（用于并发控制 + fencing）。
/// </summary>
public sealed record EventWriteCondition(
    string RunId,
    long FencingToken,
    string? ProducerEventId,
    long ExpectedConversationVersion
)
{
    public static EventWriteCondition ForRun(string runId, long fencingToken)
        => new(runId, fencingToken, null, -1);
}

/// <summary>
/// 允许服务端重复发送，前端按 sequence/eventId 幂等。
/// 浏览器仅在 sequence == localCursor + 1 且 Reducer 成功提交时推进 cursor。
/// </summary>
public sealed record SseDeliveryResult(
    long LastSentSequence,
    bool GapDetected,
    string? GapDetails
);
