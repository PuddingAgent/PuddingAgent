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
    string? ConversationId,   // 对话 ID（= sessionId，前端统一用此字段）
    long Sequence,            // 单调递增事件序号（SSE id）
    string EventType,         // 事件类型标识
    int SchemaVersion,        // Payload 的 schema 版本号（默认 1）
    string? CommandId,        // 关联的 Command（如果是 turn 级事件）
    string? TurnId,           // 关联的 Turn
    string? RunId,            // 关联的执行 Run（主代理或子代理）
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
    string? EventId = null,       // 如果提供则用于追加重试幂等，不提供则自动生成
    string? ConversationId = null // 对话 ID（当前 = sessionId）
);

/// <summary>
/// Head 推进通知——只通知 confirmed 的 sequence，不携带 Payload。
/// 由 ISessionHeadNotifier 发布，SSE 收到后从数据库读取实际事件。
/// </summary>
public sealed record SessionHeadAdvanced(
    string SessionId,
    long CommittedThroughSequence
);

/// <summary>
/// ADR-056-E 统一事件类型常量。
/// <para>
/// 旧事件名（delta/thinking/done/error/cancelled/usage/tool_call/tool_result）
/// 保留兼容但不推荐新代码使用。新代码应使用本类的常量。
/// </para>
/// </summary>
public static class SessionEventNames
{
    // Turn 生命周期
    public const string TurnAccepted = "turn.accepted";
    public const string TurnStarted = "turn.started";
    public const string TurnCompleted = "turn.completed";
    public const string TurnFailed = "turn.failed";
    public const string TurnCancelled = "turn.cancelled";

    // 助手内容流
    public const string AssistantThinkingDelta = "assistant.thinking.delta";
    public const string AssistantContentDelta = "assistant.content.delta";

    // 工具调用
    public const string ToolCallStarted = "tool.call.started";
    public const string ToolCallCompleted = "tool.call.completed";
    public const string ToolCallFailed = "tool.call.failed";

    // Token 使用
    public const string UsageRecorded = "usage.recorded";

    // 会话管理
    public const string SessionArchived = "session.archived";
    public const string SessionClosed = "session.closed";

    // 上下文
    public const string Context = "context";

    /// <summary>
    /// 旧事件名 → 新事件名的映射（用于历史兼容读取/迁移）。
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> LegacyToNew =
        new System.Collections.Generic.Dictionary<string, string>
        {
            ["delta"] = AssistantContentDelta,
            ["thinking"] = AssistantThinkingDelta,
            ["done"] = TurnCompleted,
            ["error"] = TurnFailed,
            ["cancelled"] = TurnCancelled,
            ["usage"] = UsageRecorded,
            ["tool_call"] = ToolCallStarted,
            ["tool_result"] = ToolCallCompleted,
        };

    /// <summary>
    /// 将旧事件名映射为新事件名（如果存在映射）；否则返回原名。
    /// </summary>
    public static string MapLegacy(string eventType)
        => LegacyToNew.TryGetValue(eventType, out var mapped) ? mapped : eventType;
}
