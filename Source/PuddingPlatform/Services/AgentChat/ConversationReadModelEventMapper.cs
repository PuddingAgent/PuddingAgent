using System.Text;
using System.Text.Json;
using PuddingCode.Platform;
using PuddingConversationReadModel;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// canonical Conversation Event → <see cref="ConversationEventInput"/> 的映射适配（阶段 C2）。
/// <para>
/// 归约规则本身在纯组件 <c>PuddingConversationReadModel</c>；本类只做字段读取与类型映射，
/// 不推断缺失字段、不补造终态、不做 I/O。字段名来自生产写入点（见 ConversationTranscriptFold 注释）：
/// content/thinking 为 <c>delta</c>；tool 为 toolCallId/name/arguments/output/error/exitCode；
/// 终态为 reply/errorCode/errorMessage；子代理事件由 <c>producer_component=subagent.runtime</c> 标记。
/// </para>
/// </summary>
public static class ConversationReadModelEventMapper
{
    /// <summary>单条 delta 进入纯组件前的字节上限，避免超大帧占内存；组件还会再做有界尾部裁剪。</summary>
    public const int MaxTextDeltaBytes = 64 * 1024;

    private const string SubAgentProducerComponent = "subagent.runtime";

    public static ConversationEventInput Map(ConversationEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var kind = MapKind(evt.Type);
        var isSubAgentLifecycle = IsSubAgentLifecycleKind(kind);
        var isSubAgentProduced = string.Equals(
            evt.ProducerComponent,
            SubAgentProducerComponent,
            StringComparison.Ordinal);

        var payload = evt.Payload;
        string? textDelta = kind switch
        {
            ConversationEventKind.MessageContentAppended =>
                BoundTextDelta(GetString(payload, "delta") ?? GetString(payload, "text")),
            ConversationEventKind.MessageThinkingSummaryAppended =>
                BoundTextDelta(GetString(payload, "delta")),
            _ => null,
        };

        return new ConversationEventInput
        {
            ConversationId = evt.ConversationId,
            Sequence = evt.Sequence,
            EventId = evt.EventId,
            Kind = kind,
            TurnId = EmptyToNull(evt.TurnId),
            RunId = EmptyToNull(evt.RunId),
            // 子代理产物里的正文/工具事件属于子 run；生命周期事件由组件按 run 摘要处理。
            ParentRunId = !isSubAgentLifecycle && isSubAgentProduced ? EmptyToNull(evt.RunId) : null,
            MessageId = EmptyToNull(evt.MessageId),
            SubAgentId = isSubAgentLifecycle
                ? GetString(payload, "sub_agent_id") ?? GetString(payload, "subAgentId")
                : null,
            TextDelta = textDelta,
            OccurredAtUtc = evt.OccurredAt,
        };
    }

    internal static ConversationEventKind MapKind(string type) => type switch
    {
        ConversationEventTypes.TurnStarted => ConversationEventKind.TurnStarted,
        ConversationEventTypes.TurnCompleted => ConversationEventKind.TurnCompleted,
        ConversationEventTypes.TurnFailed => ConversationEventKind.TurnFailed,
        ConversationEventTypes.TurnCancelled => ConversationEventKind.TurnCancelled,

        ConversationEventTypes.MessageContentAppended => ConversationEventKind.MessageContentAppended,
        ConversationEventTypes.MessageThinkingSummaryAppended => ConversationEventKind.MessageThinkingSummaryAppended,
        ConversationEventTypes.MessageCompleted => ConversationEventKind.MessageCompleted,
        ConversationEventTypes.MessageFailed => ConversationEventKind.MessageFailed,

        ConversationEventTypes.ToolCallRequested => ConversationEventKind.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted => ConversationEventKind.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed => ConversationEventKind.ToolCallFailed,

        ConversationEventTypes.SubAgentRunCreated => ConversationEventKind.SubAgentRunCreated,
        ConversationEventTypes.SubAgentRunStarted => ConversationEventKind.SubAgentRunStarted,
        ConversationEventTypes.SubAgentRunCompleted => ConversationEventKind.SubAgentRunCompleted,
        ConversationEventTypes.SubAgentRunFailed => ConversationEventKind.SubAgentRunFailed,
        ConversationEventTypes.SubAgentRunCancelled => ConversationEventKind.SubAgentRunCancelled,
        ConversationEventTypes.SubAgentRunTimedOut => ConversationEventKind.SubAgentRunTimedOut,
        ConversationEventTypes.SubAgentRunInterrupted => ConversationEventKind.SubAgentRunInterrupted,
        ConversationEventTypes.SubAgentRunBudgetExhausted => ConversationEventKind.SubAgentRunBudgetExhausted,

        ConversationEventTypes.ContextCompactionStarted => ConversationEventKind.ContextCompactionStarted,
        ConversationEventTypes.ContextCompactionCompleted => ConversationEventKind.ContextCompactionCompleted,
        ConversationEventTypes.ContextCompactionFailed => ConversationEventKind.ContextCompactionFailed,

        _ => ConversationEventKind.Other,
    };

    internal static bool IsSubAgentLifecycleKind(ConversationEventKind kind) => kind
        is ConversationEventKind.SubAgentRunCreated
        or ConversationEventKind.SubAgentRunStarted
        or ConversationEventKind.SubAgentRunCompleted
        or ConversationEventKind.SubAgentRunFailed
        or ConversationEventKind.SubAgentRunCancelled
        or ConversationEventKind.SubAgentRunTimedOut
        or ConversationEventKind.SubAgentRunInterrupted
        or ConversationEventKind.SubAgentRunBudgetExhausted;

    internal static string? BoundTextDelta(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= MaxTextDeltaBytes)
            return text;

        return TakeUtf8Prefix(text, MaxTextDeltaBytes);
    }

    private static string TakeUtf8Prefix(string text, int maxBytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var buffer = new byte[byteCount];
        Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);

        var length = maxBytes;
        while (length > 0 && (buffer[length] & 0xC0) == 0x80)
            length--;

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static string? GetString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!payload.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
