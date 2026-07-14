using System.Text.Json;
using PuddingCode.Observability;

namespace PuddingCode.Platform;

/// <summary>
/// 统一会话事件 Envelope——写入、读取、传输的唯一载体。
/// <para>
/// ADR-056-E：从 flat ServerSentEventFrame 迁移到此 richer 结构。
/// SSE id 字段取 Sequence；EventId 用于追加重试幂等。
/// </para>
/// </summary>
public sealed record SessionEventEnvelope(
    string EventId,           // Guid，追加重试幂等
    string SessionId,         // 所属会话
    long Sequence,            // 单调递增事件序号（SSE id）
    string EventType,         // 事件类型标识
    int SchemaVersion,        // Payload 的 schema 版本号（默认 1）
    string? CommandId,        // 关联的 Command（如果是 turn 级事件）
    string? TurnId,           // 关联的 Turn
    string? MessageId,        // 关联的消息
    string? AgentId,          // 产生此事件的 Agent
    DateTimeOffset OccurredAt,// 事件发生时间
    JsonElement Payload,      // 领域 Payload（JSON）
    RuntimeTraceContext? Trace// 可选 trace 上下文
);

/// <summary>
/// 事件写入草稿——只包含领域负载，Envelope 字段由 Writer 自动填充。
/// </summary>
public sealed record SessionEventDraft(
    string EventType,
    int SchemaVersion,
    string? CommandId,
    string? TurnId,
    string? MessageId,
    string? AgentId,
    JsonElement Payload,
    RuntimeTraceContext? Trace,
    string? EventId = null       // 如果提供则用于追加重试幂等，不提供则自动生成
);

/// <summary>
/// Head 推进通知——只通知 confirmed 的 sequence，不携带 Payload。
/// 由 ISessionHeadNotifier 发布，SSE 收到后从数据库读取实际事件。
/// </summary>
public sealed record SessionHeadAdvanced(
    string SessionId,
    long CommittedThroughSequence
);
