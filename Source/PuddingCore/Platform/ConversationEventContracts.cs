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

    /// <summary>
    /// 事件来源 AgentId（可空）。作为「按来源查看」查询维度基座。
    /// 未填时为 null（序列化不出现空字符串）。
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// 事件来源分类（可空）。存储/传输使用小写字符串。
    /// </summary>
    public ConversationEventSourceKind? SourceKind { get; init; }

    /// <summary>一等 trace_id。禁止用 correlation_id 替代。旧数据允许 null，新运行事件必须非空。</summary>
    public string? TraceId { get; init; }

    /// <summary>独立 producer_component（如 chat.acceptance / execution.journal / subagent.runtime）。agent_id 与 source_kind 都不能充当 component。</summary>
    public string? ProducerComponent { get; init; }
}

/// <summary>
/// ADR-057: Conversation 事件来源分类。
/// 序列化为小写字符串：user / agent / system / subagent / steering / compaction / goal。
/// </summary>
public enum ConversationEventSourceKind
{
    User,
    Agent,
    System,
    SubAgent,
    Steering,
    Compaction,
    Goal,
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
    JsonElement Payload,
    string? AgentId = null,
    ConversationEventSourceKind? SourceKind = null,
    string? TraceId = null,
    string? ProducerComponent = null
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
