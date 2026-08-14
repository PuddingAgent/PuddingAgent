using System.Text;
using System.Text.Json;

namespace PuddingCode.Platform;

// ═══════════════════════════════════════════════════════════════
// ConversationTranscriptFold — ADR-057 方案2：统一语义投影 Fold（纯函数）
//
// 把「有序 conversation_events」折叠为按 TurnId 分组的语义转录。
// 只读复用 ConversationEventTypes 常量；不桥接 delta/thinking/done
// 等 SSE 传输帧，也不依赖 SQLite / 实体 / DI / I/O。
//
// 字段名全部来自生产代码实际写入点（见类内注释的证据来源），
// 缺失字段一律 null 防御、不抛异常（TryGetProperty 模式）。
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 工具调用最终状态。由事件类型推导，不臆造额外终态。
/// </summary>
public enum ConversationToolCallStatus
{
    /// <summary>tool.call.requested 已写入，尚未收到 completed/failed。</summary>
    Requested,

    /// <summary>tool.call.completed 已写入（含可能非零 exitCode/error 的结果细节）。</summary>
    Completed,

    /// <summary>tool.call.failed 已写入。</summary>
    Failed,
}

/// <summary>
/// 折叠片段证据坐标，用于 query_session_logs 等证据引用定位。
/// </summary>
public sealed record EvidenceRef(
    string? EventId,
    long Sequence
);

/// <summary>
/// 单个工具调用（requested / completed / failed 按 callId 关联后的投影）。
/// </summary>
public sealed record ConversationToolCall
{
    /// <summary>工具调用稳定 ID（toolCallId 字段）。缺失时为 null。</summary>
    public string? CallId { get; init; }

    /// <summary>工具名（name 字段）。</summary>
    public string? Name { get; init; }

    /// <summary>调用参数（arguments 字段，JSON 字符串原样透传）。</summary>
    public string? Arguments { get; init; }

    /// <summary>工具输出（output 字段）。</summary>
    public string? Output { get; init; }

    /// <summary>工具错误（error 字段）。</summary>
    public string? Error { get; init; }

    /// <summary>退出码（exitCode 字段）。</summary>
    public int? ExitCode { get; init; }

    /// <summary>最终状态。</summary>
    public ConversationToolCallStatus Status { get; init; }

    /// <summary>tool.call.requested 证据坐标。</summary>
    public EvidenceRef? RequestedEvidence { get; init; }

    /// <summary>tool.call.completed / failed 证据坐标。</summary>
    public EvidenceRef? CompletedEvidence { get; init; }
}

/// <summary>
/// Turn 内 token 用量汇总（跨 usage.recorded 累加）。
/// </summary>
public sealed record ConversationUsageSummary
{
    /// <summary>Prompt tokens 合计。</summary>
    public long? PromptTokens { get; init; }

    /// <summary>Completion tokens 合计。</summary>
    public long? CompletionTokens { get; init; }

    /// <summary>Total tokens 合计（缺失时由 prompt+completion 推导）。</summary>
    public long? TotalTokens { get; init; }

    /// <summary>累计的 usage.recorded 事件数。</summary>
    public int EventCount { get; init; }

    /// <summary>首个 usage.recorded 证据坐标。</summary>
    public EvidenceRef? FirstEvidence { get; init; }

    /// <summary>末个 usage.recorded 证据坐标。</summary>
    public EvidenceRef? LastEvidence { get; init; }
}

/// <summary>
/// 按 TurnId 分组折叠出的单个 turn 语义转录。
/// </summary>
public sealed record ConversationTurn
{
    /// <summary>Turn 稳定 ID（来自事件信封 TurnId）。</summary>
    public required string TurnId { get; init; }

    /// <summary>turn 内首个事件 sequence（用于 turn 排序）。</summary>
    public long FirstSequence { get; init; }

    /// <summary>turn 内末个事件 sequence。</summary>
    public long LastSequence { get; init; }

    /// <summary>用户消息文本（message.created role=user 的 content）。</summary>
    public string? UserMessageText { get; init; }

    /// <summary>用户消息证据坐标。</summary>
    public EvidenceRef? UserMessageEvidence { get; init; }

    /// <summary>助手正文（message.content.appended 按 sequence 拼接）。</summary>
    public string? AssistantText { get; init; }

    /// <summary>助手正文首个 delta 证据坐标。</summary>
    public EvidenceRef? AssistantTextEvidence { get; init; }

    /// <summary>助手正文末个 delta 证据坐标。</summary>
    public EvidenceRef? AssistantTextEndEvidence { get; init; }

    /// <summary>推理摘要（message.thinking_summary.appended 按 sequence 拼接）。</summary>
    public string? ThinkingSummary { get; init; }

    /// <summary>推理摘要首个增量证据坐标。</summary>
    public EvidenceRef? ThinkingEvidence { get; init; }

    /// <summary>推理摘要末个增量证据坐标。</summary>
    public EvidenceRef? ThinkingEndEvidence { get; init; }

    /// <summary>工具调用列表（保持 requested 首次出现顺序）。</summary>
    public IReadOnlyList<ConversationToolCall> ToolCalls { get; init; } = [];

    /// <summary>token 用量汇总（无 usage.recorded 时为 null）。</summary>
    public ConversationUsageSummary? Usage { get; init; }

    /// <summary>turn 终态（completed / failed / cancelled；未终态为 null）。</summary>
    public TurnTerminalKind? TerminalKind { get; init; }

    /// <summary>turn.completed 的 reply 字段（完整回复兜底）。</summary>
    public string? Reply { get; init; }

    /// <summary>turn.failed 的错误码。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>turn.failed 的错误信息。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>终态事件证据坐标。</summary>
    public EvidenceRef? TerminalEvidence { get; init; }
}

/// <summary>
/// 整段会话的语义转录投影。
/// </summary>
public sealed record ConversationTranscript(
    string ConversationId,
    IReadOnlyList<ConversationTurn> Turns
);

/// <summary>
/// 纯函数 Fold：将 ConversationEvent 流折叠为语义转录。
/// </summary>
public static class ConversationTranscriptFold
{
    /// <summary>
    /// 折叠事件流。输入无需预排序；内部按 Sequence 稳定排序，
    /// turn 按「首个事件 sequence」保持顺序。
    /// </summary>
    public static ConversationTranscript Fold(IEnumerable<ConversationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events
            .Where(e => e is not null)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.EventId ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var builders = new Dictionary<string, TurnAccumulator>(StringComparer.Ordinal);
        var turnOrder = new List<string>();

        foreach (var evt in ordered)
        {
            var turnId = evt.TurnId ?? string.Empty;
            if (!builders.TryGetValue(turnId, out var acc))
            {
                acc = new TurnAccumulator(turnId);
                builders[turnId] = acc;
                turnOrder.Add(turnId);
            }
            acc.Apply(evt);
        }

        var turns = new List<ConversationTurn>(turnOrder.Count);
        foreach (var turnId in turnOrder)
        {
            turns.Add(builders[turnId].Build());
        }

        var conversationId = ordered.Count > 0 ? ordered[0].ConversationId : string.Empty;
        return new ConversationTranscript(conversationId, turns);
    }

    // ── 可变累加器（Build 时转为不可变 record）───────────────────

    private sealed class TurnAccumulator
    {
        private readonly string _turnId;
        private readonly StringBuilder _assistantText = new();
        private readonly StringBuilder _thinking = new();
        private readonly List<ToolCallAccumulator> _toolCalls = [];
        private readonly Dictionary<string, ToolCallAccumulator> _toolCallByKey = new(StringComparer.Ordinal);

        private long _firstSequence = long.MaxValue;
        private long _lastSequence = long.MinValue;

        private string? _userText;
        private EvidenceRef? _userEvidence;

        private EvidenceRef? _assistantFirst;
        private EvidenceRef? _assistantLast;
        private EvidenceRef? _thinkingFirst;
        private EvidenceRef? _thinkingLast;

        private long? _promptTokens;
        private long? _completionTokens;
        private long? _totalTokens;
        private int _usageEventCount;
        private EvidenceRef? _usageFirst;
        private EvidenceRef? _usageLast;

        private TurnTerminalKind? _terminalKind;
        private EvidenceRef? _terminalEvidence;
        private string? _reply;
        private string? _errorCode;
        private string? _errorMessage;

        public TurnAccumulator(string turnId) => _turnId = turnId;

        public void Apply(ConversationEvent evt)
        {
            if (evt.Sequence < _firstSequence) _firstSequence = evt.Sequence;
            if (evt.Sequence > _lastSequence) _lastSequence = evt.Sequence;

            switch (evt.Type)
            {
                case ConversationEventTypes.MessageCreated:
                    ApplyMessageCreated(evt);
                    break;
                case ConversationEventTypes.MessageContentAppended:
                    ApplyContentAppended(evt);
                    break;
                case ConversationEventTypes.MessageThinkingSummaryAppended:
                    ApplyThinkingAppended(evt);
                    break;
                case ConversationEventTypes.ToolCallRequested:
                    ApplyToolCallRequested(evt);
                    break;
                case ConversationEventTypes.ToolCallCompleted:
                    ApplyToolCallCompleted(evt);
                    break;
                case ConversationEventTypes.ToolCallFailed:
                    ApplyToolCallFailed(evt);
                    break;
                case ConversationEventTypes.UsageRecorded:
                    ApplyUsageRecorded(evt);
                    break;
                case ConversationEventTypes.TurnCompleted:
                    ApplyTerminal(evt, TurnTerminalKind.Completed);
                    break;
                case ConversationEventTypes.TurnFailed:
                    ApplyTerminal(evt, TurnTerminalKind.Failed);
                    break;
                case ConversationEventTypes.TurnCancelled:
                    ApplyTerminal(evt, TurnTerminalKind.Cancelled);
                    break;
                default:
                    // 其他事件类型（turn.accepted/started、subagent.run.*、compaction.* 等）本轮忽略。
                    break;
            }
        }

        public ConversationTurn Build()
        {
            var assistantText = _assistantText.Length > 0 ? _assistantText.ToString() : _reply;

            ConversationUsageSummary? usage = null;
            if (_usageEventCount > 0)
            {
                var total = _totalTokens ?? SumNullable(_promptTokens, _completionTokens);
                usage = new ConversationUsageSummary
                {
                    PromptTokens = _promptTokens,
                    CompletionTokens = _completionTokens,
                    TotalTokens = total,
                    EventCount = _usageEventCount,
                    FirstEvidence = _usageFirst,
                    LastEvidence = _usageLast,
                };
            }

            return new ConversationTurn
            {
                TurnId = _turnId,
                FirstSequence = _firstSequence == long.MaxValue ? 0 : _firstSequence,
                LastSequence = _lastSequence == long.MinValue ? 0 : _lastSequence,
                UserMessageText = _userText,
                UserMessageEvidence = _userEvidence,
                AssistantText = assistantText,
                AssistantTextEvidence = _assistantFirst,
                AssistantTextEndEvidence = _assistantLast,
                ThinkingSummary = _thinking.Length > 0 ? _thinking.ToString() : null,
                ThinkingEvidence = _thinkingFirst,
                ThinkingEndEvidence = _thinkingLast,
                ToolCalls = _toolCalls.Select(t => t.ToRecord()).ToList(),
                Usage = usage,
                TerminalKind = _terminalKind,
                Reply = _reply,
                ErrorCode = _errorCode,
                ErrorMessage = _errorMessage,
                TerminalEvidence = _terminalEvidence,
            };
        }

        private static long? SumNullable(long? a, long? b)
            => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

        private void ApplyMessageCreated(ConversationEvent evt)
        {
            // 证据来源：message.created 契约声明含 role；生产侧当前无写入点（见交付报告不确定项）。
            // 防御性读取 role 与 content（以及 text/message 兜底），缺失不抛。
            var role = GetString(evt.Payload, "role");
            var content = GetString(evt.Payload, "content")
                ?? GetString(evt.Payload, "text")
                ?? GetString(evt.Payload, "message");
            if (string.IsNullOrEmpty(content))
                return;

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                _userText = content;
                _userEvidence = Evidence(evt);
            }
            else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                     && _assistantText.Length == 0)
            {
                // assistant message.created 作为无 delta 时的正文兜底基座。
                _assistantText.Append(content);
                _assistantFirst ??= Evidence(evt);
            }
        }

        private void ApplyContentAppended(ConversationEvent evt)
        {
            // 证据来源：TurnOutputChunker 写入 {"delta": "<aggregated>"}。
            var delta = GetString(evt.Payload, "delta");
            if (delta is null)
                return;

            var evidence = Evidence(evt);
            _assistantFirst ??= evidence;
            _assistantText.Append(delta);
            _assistantLast = evidence;
        }

        private void ApplyThinkingAppended(ConversationEvent evt)
        {
            // 证据来源：TurnOutputChunker 写入 {"delta": "<aggregated>"}。
            var delta = GetString(evt.Payload, "delta");
            if (delta is null)
                return;

            var evidence = Evidence(evt);
            _thinkingFirst ??= evidence;
            _thinking.Append(delta);
            _thinkingLast = evidence;
        }

        private void ApplyToolCallRequested(ConversationEvent evt)
        {
            // 证据来源：AgentExecutionService.Streaming 写入 { name, arguments, toolCallId }。
            var callId = GetString(evt.Payload, "toolCallId");
            var call = GetOrCreateToolCall(callId);
            call.CallId ??= callId;
            call.Name ??= GetString(evt.Payload, "name");
            call.Arguments ??= GetString(evt.Payload, "arguments");
            call.RequestedEvidence ??= Evidence(evt);
        }

        private void ApplyToolCallCompleted(ConversationEvent evt)
        {
            // 证据来源：AgentExecutionService.Streaming 写入 { name, toolCallId, exitCode, output, error }。
            var callId = GetString(evt.Payload, "toolCallId");
            var call = GetOrCreateToolCall(callId);
            ApplyToolCallResult(call, evt, failed: false);
        }

        private void ApplyToolCallFailed(ConversationEvent evt)
        {
            // 证据来源：tool.call.failed 当前无生产写入点；防御性按同一字段契约读取。
            var callId = GetString(evt.Payload, "toolCallId");
            var call = GetOrCreateToolCall(callId);
            ApplyToolCallResult(call, evt, failed: true);
        }

        private void ApplyToolCallResult(ToolCallAccumulator call, ConversationEvent evt, bool failed)
        {
            call.CallId ??= GetString(evt.Payload, "toolCallId");
            call.Name ??= GetString(evt.Payload, "name");
            call.Output ??= GetString(evt.Payload, "output");
            call.Error ??= GetString(evt.Payload, "error");
            call.ExitCode ??= GetInt(evt.Payload, "exitCode") ?? GetInt(evt.Payload, "exit_code");
            call.CompletedEvidence ??= Evidence(evt);

            if (failed)
                call.Status = ConversationToolCallStatus.Failed;
            else if (call.Status == ConversationToolCallStatus.Requested)
                call.Status = ConversationToolCallStatus.Completed;
        }

        private ToolCallAccumulator GetOrCreateToolCall(string? callId)
        {
            if (!string.IsNullOrWhiteSpace(callId)
                && _toolCallByKey.TryGetValue(callId, out var existing))
            {
                return existing;
            }

            var call = new ToolCallAccumulator { CallId = callId };
            _toolCalls.Add(call);
            if (!string.IsNullOrWhiteSpace(callId))
                _toolCallByKey[callId] = call;
            return call;
        }

        private void ApplyUsageRecorded(ConversationEvent evt)
        {
            // 证据来源：TurnExecutorAdapter.CreateUsageRecordedPayload（schemaVersion=2）：
            //   { usage: {promptTokens,completionTokens,totalTokens,...}, providerId, profileId, modelId, role, invocationIndex }
            // 兼容 schemaVersion=1（原始 usage 帧直接作为顶层字段）。
            var (prompt, completion, total) = ReadUsageTokens(evt.Payload);
            if (prompt is null && completion is null && total is null)
                return;

            _usageEventCount++;
            if (prompt is not null) _promptTokens = (_promptTokens ?? 0) + prompt.Value;
            if (completion is not null) _completionTokens = (_completionTokens ?? 0) + completion.Value;
            if (total is not null) _totalTokens = (_totalTokens ?? 0) + total.Value;

            var evidence = Evidence(evt);
            _usageFirst ??= evidence;
            _usageLast = evidence;
        }

        private void ApplyTerminal(ConversationEvent evt, TurnTerminalKind kind)
        {
            // 证据来源：SqliteExecutionJournal.BuildTerminalPayload 写入
            //   { kind, errorCode, errorMessage, reply }。
            _terminalKind = kind;
            _terminalEvidence = Evidence(evt);
            _reply = GetString(evt.Payload, "reply");
            _errorCode = GetString(evt.Payload, "errorCode") ?? GetString(evt.Payload, "code");
            _errorMessage = GetString(evt.Payload, "errorMessage") ?? GetString(evt.Payload, "message");
        }

        private static EvidenceRef Evidence(ConversationEvent evt) => new(evt.EventId, evt.Sequence);
    }

    private sealed class ToolCallAccumulator
    {
        public string? CallId;
        public string? Name;
        public string? Arguments;
        public string? Output;
        public string? Error;
        public int? ExitCode;
        public ConversationToolCallStatus Status = ConversationToolCallStatus.Requested;
        public EvidenceRef? RequestedEvidence;
        public EvidenceRef? CompletedEvidence;

        public ConversationToolCall ToRecord() => new()
        {
            CallId = CallId,
            Name = Name,
            Arguments = Arguments,
            Output = Output,
            Error = Error,
            ExitCode = ExitCode,
            Status = Status,
            RequestedEvidence = RequestedEvidence,
            CompletedEvidence = CompletedEvidence,
        };
    }

    // ── 防御性 JSON 读取（TryGetProperty 模式，绝不抛异常）──────

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static long? GetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static (long? Prompt, long? Completion, long? Total) ReadUsageTokens(JsonElement payload)
    {
        var src = payload;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("usage", out var usageObj)
            && usageObj.ValueKind == JsonValueKind.Object)
        {
            src = usageObj;
        }

        var prompt = GetLong(src, "promptTokens") ?? GetLong(src, "PromptTokens");
        var completion = GetLong(src, "completionTokens") ?? GetLong(src, "CompletionTokens");
        var total = GetLong(src, "totalTokens") ?? GetLong(src, "TotalTokens");
        return (prompt, completion, total);
    }
}
