using System.Text.Json;
using PuddingCode.Diagnostics;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 共享 Conversation 诊断事件投影器（无状态，注册为 Singleton）。
/// 作为 trace-report / RuntimeTimeline / E2E Evidence 三读者唯一的 Payload 解析/状态映射/字段投影入口。
///
/// 投影器永不抛异常：所有 JSON 读取前先判 <c>Payload.ValueKind == Object</c>，
/// 解析/取值异常一律吞掉并返回 null 或默认值；脱敏仍由上层 IDiagnosticRedactor 负责（投影器产出原文）。
/// </summary>
public sealed class ConversationDiagnosticEventProjector : IConversationDiagnosticEventProjector
{
    // ── 有界常量（§6.1，集中定义便于调参）──────────────────
    private const int MaxSummaryLength = 512;
    private const int MaxErrorLength = 1024;
    private const int MaxFieldLength = 256;
    private const string TruncationMarker = "… (truncated)";

    private const string KindConversationEvent = "conversation_event";
    private const string DefaultComponent = "conversation";
    private const string DefaultStatus = "recorded";

    // ── 终态集合（§5 汇总）─────────────────────────────────
    private static readonly HashSet<string> TerminalTypes = new(StringComparer.Ordinal)
    {
        ConversationEventTypes.TurnCompleted,
        ConversationEventTypes.TurnFailed,
        ConversationEventTypes.TurnCancelled,
        ConversationEventTypes.MessageCompleted,
        ConversationEventTypes.MessageFailed,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
        ConversationEventTypes.RunLeaseLost,
        ConversationEventTypes.ContextCompactionCompleted,
        ConversationEventTypes.ContextCompactionFailed,
        ConversationEventTypes.ConversationArchived,
        ConversationEventTypes.SubAgentRunCompleted,
        ConversationEventTypes.SubAgentRunBudgetExhausted,
        ConversationEventTypes.SubAgentRunFailed,
        ConversationEventTypes.SubAgentRunCancelled,
        ConversationEventTypes.SubAgentRunTimedOut,
        ConversationEventTypes.SubAgentRunInterrupted,
    };

    // ── 事件类型 → Status（§5 完整映射表：45 canonical + 2 非 canonical）──
    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.Ordinal)
    {
        // 5.1 Turn 生命周期
        [ConversationEventTypes.TurnAccepted] = "accepted",
        [ConversationEventTypes.TurnStarted] = "started",
        [ConversationEventTypes.TurnWaitingForTool] = "waiting_for_tool",
        [ConversationEventTypes.TurnCompleted] = "completed",
        [ConversationEventTypes.TurnFailed] = "failed",
        [ConversationEventTypes.TurnCancelled] = "cancelled",
        [ConversationEventTypes.TurnCancelRequested] = "cancel_requested",

        // 5.2 Message 生命周期
        [ConversationEventTypes.MessageCreated] = "created",
        [ConversationEventTypes.MessageStarted] = "started",
        [ConversationEventTypes.MessageContentAppended] = "running",
        [ConversationEventTypes.MessageThinkingSummaryAppended] = "running",
        [ConversationEventTypes.MessageCompleted] = "completed",
        [ConversationEventTypes.MessageFailed] = "failed",

        // 5.3 Tool 调用
        [ConversationEventTypes.ToolCallRequested] = "started",
        [ConversationEventTypes.ToolCallCompleted] = "completed",
        [ConversationEventTypes.ToolCallFailed] = "failed",

        // 5.4 Usage / Run / Error
        [ConversationEventTypes.UsageRecorded] = "recorded",
        [ConversationEventTypes.RunLeaseLost] = "lease_lost",
        [ConversationEventTypes.ErrorRecorded] = "failed",

        // 5.5 Context / Steering
        [ConversationEventTypes.ContextCompactionStarted] = "started",
        [ConversationEventTypes.ContextCompactionCompleted] = "completed",
        [ConversationEventTypes.ContextCompactionFailed] = "failed",
        [ConversationEventTypes.ContextAssembled] = "recorded",
        [ConversationEventTypes.SteeringInjected] = "recorded",

        // 5.6 Conversation 生命周期
        [ConversationEventTypes.ConversationOpened] = "opened",
        [ConversationEventTypes.ConversationParticipantBound] = "recorded",
        [ConversationEventTypes.ConversationArchived] = "archived",

        // 5.7 SubAgent Run 生命周期
        [ConversationEventTypes.SubAgentRunCreated] = "created",
        [ConversationEventTypes.SubAgentRunStarted] = "started",
        [ConversationEventTypes.SubAgentBudgetNotice] = "notice",
        [ConversationEventTypes.SubAgentRunContextAssembled] = "running",
        [ConversationEventTypes.SubAgentRunCompleted] = "completed",
        [ConversationEventTypes.SubAgentRunBudgetExhausted] = "budget_exhausted",
        [ConversationEventTypes.SubAgentRunFailed] = "failed",
        [ConversationEventTypes.SubAgentRunCancelled] = "cancelled",
        [ConversationEventTypes.SubAgentRunTimedOut] = "timed_out",
        [ConversationEventTypes.SubAgentRunInterrupted] = "interrupted",

        // 5.8 SubAgent 子步骤（round / llm / tool，非 run 终态）
        [ConversationEventTypes.SubAgentRoundStarted] = "started",
        [ConversationEventTypes.SubAgentRoundCompleted] = "completed",
        [ConversationEventTypes.SubAgentLlmStarted] = "started",
        [ConversationEventTypes.SubAgentLlmCompleted] = "completed",
        [ConversationEventTypes.SubAgentLlmFailed] = "failed",
        [ConversationEventTypes.SubAgentToolStarted] = "started",
        [ConversationEventTypes.SubAgentToolCompleted] = "completed",
        [ConversationEventTypes.SubAgentToolFailed] = "failed",

        // 5.9 非 canonical（仍在写入）
        ["context"] = "recorded",
        ["terminal"] = "recorded",
    };

    // ── 错误语义事件类型（§6.3 白名单）──────────────────────
    private static readonly HashSet<string> ErrorSemanticTypes = new(StringComparer.Ordinal)
    {
        ConversationEventTypes.TurnFailed,
        ConversationEventTypes.TurnCancelled,
        ConversationEventTypes.MessageFailed,
        ConversationEventTypes.ToolCallFailed,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ErrorRecorded,
        ConversationEventTypes.ContextCompactionFailed,
        ConversationEventTypes.SubAgentLlmFailed,
        ConversationEventTypes.SubAgentToolFailed,
        ConversationEventTypes.SubAgentRunFailed,
    };

    // ═══════════════════════════════════════════════════════════
    // 核心单事件投影（§4 字段映射表）
    // ═══════════════════════════════════════════════════════════

    public RuntimeTimelineItemDto Project(ConversationEvent evt)
    {
        var terminal = IsTerminalType(evt.Type);
        return new RuntimeTimelineItemDto
        {
            Id = evt.EventId,
            Kind = KindConversationEvent,
            Component = ComponentOf(evt),
            Operation = evt.Type,
            Status = MapStatus(evt.Type),
            WorkspaceId = evt.WorkspaceId,
            SessionId = evt.ConversationId,
            AgentInstanceId = evt.AgentId,
            RunId = evt.RunId,
            EventId = evt.EventId,
            TraceId = evt.TraceId,
            CorrelationId = evt.CorrelationId,
            StartedAtUtc = evt.OccurredAt,
            CompletedAtUtc = terminal ? evt.OccurredAt : null,
            DurationMs = null,
            Summary = ExtractSummary(evt),
            Error = ExtractError(evt),
            Metadata = BuildMetadata(evt),
        };
    }

    public IReadOnlyList<RuntimeTimelineItemDto> Project(IEnumerable<ConversationEvent> events)
        => events is null
            ? Array.Empty<RuntimeTimelineItemDto>()
            : events.Select(Project).ToList();

    // ═══════════════════════════════════════════════════════════
    // 状态 / 终态映射（§5）
    // ═══════════════════════════════════════════════════════════

    public string MapStatus(string eventType)
        => StatusMap.TryGetValue(eventType, out var status) ? status : DefaultStatus;

    public bool IsTerminalType(string eventType)
        => TerminalTypes.Contains(eventType);

    // ═══════════════════════════════════════════════════════════
    // 有界 Summary 提取（§6.2）
    // ═══════════════════════════════════════════════════════════

    public string? ExtractSummary(ConversationEvent evt)
    {
        string? summary = evt.Type switch
        {
            ConversationEventTypes.TurnCompleted
                => BoundedField(ReadString(evt.Payload, "reply", "kind")),

            ConversationEventTypes.TurnFailed or ConversationEventTypes.TurnCancelled
                => BoundedField(ReadString(evt.Payload, "errorMessage", "message", "kind")),

            ConversationEventTypes.MessageContentAppended
                => BoundedField(ReadString(evt.Payload, "content", "delta")),

            ConversationEventTypes.MessageThinkingSummaryAppended
                => BoundedField(ReadString(evt.Payload, "summary", "thinking")),

            ConversationEventTypes.ToolCallRequested
                => BoundedField(ReadString(evt.Payload, "name")),

            ConversationEventTypes.ToolCallCompleted
                => BuildToolCallSummary(evt.Payload, failed: false),

            ConversationEventTypes.ToolCallFailed
                => BuildToolCallSummary(evt.Payload, failed: true),

            ConversationEventTypes.UsageRecorded
                => BuildUsageSummary(evt.Payload),

            ConversationEventTypes.ErrorRecorded
                => BoundedField(ReadString(evt.Payload, "errorMessage", "message")),

            _ when evt.Type.StartsWith("subagent.", StringComparison.Ordinal)
                => BuildSubAgentSummary(evt.Payload, evt.Type),

            _ => null,
        };

        return summary is null ? null : Truncate(summary, MaxSummaryLength);
    }

    // ═══════════════════════════════════════════════════════════
    // 有界 Error 提取（§6.3）
    // ═══════════════════════════════════════════════════════════

    public string? ExtractError(ConversationEvent evt)
    {
        if (!ErrorSemanticTypes.Contains(evt.Type))
            return null;

        var error = ReadString(evt.Payload, "errorMessage", "error", "message", "code");
        return error is null ? null : Truncate(error, MaxErrorLength);
    }

    // ═══════════════════════════════════════════════════════════
    // 聚合辅助（trace-report 复用）
    // ═══════════════════════════════════════════════════════════

    public UsageProjection? TryProjectUsage(ConversationEvent evt)
    {
        if (evt.Type != ConversationEventTypes.UsageRecorded)
            return null;

        var payload = evt.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        var providerId = BoundedField(ReadString(payload, "providerId"));
        var modelId = BoundedField(ReadString(payload, "modelId"));
        var endpoint = BoundedField(ReadString(payload, "endpoint"));

        var (inputTokens, outputTokens, totalTokens) = ReadUsageTokens(payload);
        var durationMs = ReadLong(payload, "durationMs")
            ?? ReadLong(UsageObject(payload), "durationMs");

        if (providerId is null && modelId is null && endpoint is null
            && inputTokens is null && outputTokens is null && totalTokens is null
            && durationMs is null)
        {
            return null;
        }

        return new UsageProjection(
            providerId, modelId, endpoint,
            inputTokens, outputTokens, totalTokens, durationMs);
    }

    public ToolCallProjection? TryProjectToolCall(ConversationEvent evt)
    {
        if (evt.Type is not (ConversationEventTypes.ToolCallRequested
            or ConversationEventTypes.ToolCallCompleted
            or ConversationEventTypes.ToolCallFailed))
        {
            return null;
        }

        var payload = evt.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        var name = BoundedField(ReadString(payload, "name"));
        var exitCode = ReadInt(payload, "exitCode", "exit_code");
        var output = BoundedField(ReadString(payload, "output"));
        var error = BoundedField(ReadString(payload, "error"));

        if (name is null && exitCode is null && output is null && error is null)
            return null;

        return new ToolCallProjection(name, exitCode, output, error);
    }

    public string? ExtractSubAgentId(ConversationEvent evt)
    {
        if (!evt.Type.StartsWith("subagent.", StringComparison.Ordinal))
            return null;

        return BoundedField(ReadString(evt.Payload, "subAgentId", "subagentId"));
    }

    // ═══════════════════════════════════════════════════════════
    // 私有辅助
    // ═══════════════════════════════════════════════════════════

    private static string ComponentOf(ConversationEvent evt)
        => evt.ProducerComponent ?? SourceKindToString(evt.SourceKind) ?? DefaultComponent;

    private static string? SourceKindToString(ConversationEventSourceKind? kind) => kind switch
    {
        ConversationEventSourceKind.User => "user",
        ConversationEventSourceKind.Agent => "agent",
        ConversationEventSourceKind.System => "system",
        ConversationEventSourceKind.SubAgent => "subagent",
        ConversationEventSourceKind.Steering => "steering",
        ConversationEventSourceKind.Compaction => "compaction",
        ConversationEventSourceKind.Goal => "goal",
        _ => null,
    };

    private static IReadOnlyDictionary<string, string> BuildMetadata(ConversationEvent evt)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sequence"] = evt.Sequence.ToString(),
            ["turn_id"] = evt.TurnId,
            ["schema_version"] = evt.SchemaVersion.ToString(),
        };
        AddIfNotNull(metadata, "command_id", evt.CommandId);
        AddIfNotNull(metadata, "message_id", evt.MessageId);
        AddIfNotNull(metadata, "causation_id", evt.CausationId);
        AddIfNotNull(metadata, "correlation_id", evt.CorrelationId);
        AddIfNotNull(metadata, "agent_id", evt.AgentId);
        AddIfNotNull(metadata, "source_kind", SourceKindToString(evt.SourceKind));
        AddIfNotNull(metadata, "producer_component", evt.ProducerComponent);
        return metadata;
    }

    private static void AddIfNotNull(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            metadata[key] = value;
    }

    private static string? BuildToolCallSummary(JsonElement payload, bool failed)
    {
        var name = BoundedField(ReadString(payload, "name"));
        var exitCode = failed ? null : ReadInt(payload, "exitCode", "exit_code");
        var error = BoundedField(ReadString(payload, "error"));

        var parts = new List<string>(3);
        if (name is not null)
            parts.Add("name=" + name);
        if (exitCode is not null)
            parts.Add("exitCode=" + exitCode.Value);
        if (error is not null)
            parts.Add("error=" + error);

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? BuildUsageSummary(JsonElement payload)
    {
        var model = BoundedField(ReadString(payload, "modelId", "providerId"));
        var (_, _, totalTokens) = ReadUsageTokens(payload);

        var parts = new List<string>(2);
        if (model is not null)
            parts.Add(model);
        if (totalTokens is not null)
            parts.Add("tokens=" + totalTokens.Value);

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? BuildSubAgentSummary(JsonElement payload, string type)
    {
        var name = BoundedField(ReadString(payload, "name", "subAgentId", "subagentId", "kind"));
        return name ?? type;
    }

    /// <summary>
    /// 读取 usage token 三元组。兼容 v2（token 字段嵌套在 payload.usage 下）与 v1（顶层字段）。
    /// 字段名兼容：inputTokens/promptTokens、outputTokens/completionTokens、totalTokens。
    /// </summary>
    private static (long? Input, long? Output, long? Total) ReadUsageTokens(JsonElement payload)
    {
        var usage = UsageObject(payload);
        var input = ReadLong(usage, "inputTokens", "promptTokens");
        var output = ReadLong(usage, "outputTokens", "completionTokens");
        var total = ReadLong(usage, "totalTokens");
        if (total is null && input is not null && output is not null)
            total = input.Value + output.Value;
        return (input, output, total);
    }

    private static JsonElement UsageObject(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            return usage;
        }
        return payload;
    }

    private static string? ReadString(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text is not null)
                    return text;
            }
        }
        return null;
    }

    private static long? ReadLong(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
                return number;
        }
        return null;
    }

    private static int? ReadInt(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
                return number;
        }
        return null;
    }

    private static string? BoundedField(string? value)
        => value is null ? null : Truncate(value, MaxFieldLength);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + TruncationMarker;
}
