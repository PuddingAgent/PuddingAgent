namespace PuddingCode.Observability;

public static class RuntimeActivityComponents
{
    public const string Connector = "connector";
    public const string EventQueue = "event_queue";
    public const string EventDispatcher = "event_dispatcher";
    public const string SessionState = "session_state";
    public const string AgentExecution = "agent_execution";
    public const string ContextPipeline = "context_pipeline";
    public const string LlmGateway = "llm_gateway";
    public const string ToolRunner = "tool_runner";
    public const string SubAgent = "sub_agent";
    public const string Memory = "memory";
    public const string HookSystem = "hook_system";
    public const string SmartToolWrapper = "smart_tool_wrapper";
}

public static class RuntimeActivityStatuses
{
    public const string Started = "started";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Deferred = "deferred";
    public const string Retried = "retried";
}

/// <summary>
/// Canonical user-message processing stages used to align backend traces,
/// metrics, frontend diagnostics, and user-visible progress.
/// </summary>
public static class RuntimePipelineStages
{
    public const string Request = "request";
    public const string Routing = "routing";
    public const string Dispatch = "dispatch";
    public const string Context = "context";
    public const string Tool = "tool";
    public const string LlmPrepare = "llm_prepare";
    public const string LlmProvider = "llm_provider";
    public const string StreamPersist = "stream_persist";
    public const string StreamDeliver = "stream_deliver";
    public const string UiRender = "ui_render";
    public const string Complete = "complete";
    public const string Error = "error";
    public const string Unknown = "unknown";

    private static readonly IReadOnlyDictionary<string, int> Orders = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Request] = 10,
        [Routing] = 20,
        [Dispatch] = 30,
        [Context] = 40,
        [Tool] = 50,
        [LlmPrepare] = 60,
        [LlmProvider] = 70,
        [StreamPersist] = 80,
        [StreamDeliver] = 90,
        [UiRender] = 100,
        [Complete] = 110,
        [Error] = 900,
        [Unknown] = 999,
    };

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Request] = "接收用户请求",
        [Routing] = "解析会话路由",
        [Dispatch] = "调度 Agent 执行",
        [Context] = "组装上下文",
        [Tool] = "调用工具",
        [LlmPrepare] = "准备模型调用",
        [LlmProvider] = "等待模型输出",
        [StreamPersist] = "保存输出帧",
        [StreamDeliver] = "推送输出帧",
        [UiRender] = "前端渲染输出",
        [Complete] = "完成会话响应",
        [Error] = "处理异常",
        [Unknown] = "未分类阶段",
    };

    public static int GetOrder(string? stage)
        => stage is not null && Orders.TryGetValue(stage, out var order) ? order : Orders[Unknown];

    public static string GetLabel(string? stage)
        => stage is not null && Labels.TryGetValue(stage, out var label) ? label : Labels[Unknown];

    public static bool IsCanonical(string? stage)
        => !string.IsNullOrWhiteSpace(stage) && Orders.ContainsKey(stage);

    public static string Normalize(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return Unknown;
        if (IsCanonical(stage))
            return stage;
        return Resolve(component: null, operation: stage, category: null, name: null, status: null);
    }

    public static string ResolveForActivity(string? component, string? operation, string? status = null)
        => Resolve(component, operation, category: null, name: null, status);

    public static string ResolveForMetric(string? category, string? name, string? status = null)
        => Resolve(component: null, operation: null, category, name, status);

    public static IReadOnlyDictionary<string, string> Enrich(
        IReadOnlyDictionary<string, string>? metadata,
        string stage)
    {
        var enriched = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        var rawStage = enriched.TryGetValue("stage", out var existingStage) && !string.IsNullOrWhiteSpace(existingStage)
            ? existingStage
            : stage;
        var normalizedStage = Normalize(rawStage);
        if (normalizedStage == Unknown && !string.IsNullOrWhiteSpace(rawStage) && !IsCanonical(rawStage))
            throw new InvalidOperationException($"Runtime pipeline stage is not canonical or mappable: '{rawStage}'.");

        if (!IsCanonical(rawStage) && !string.IsNullOrWhiteSpace(rawStage))
            enriched["stage_detail"] = rawStage;

        enriched["stage"] = normalizedStage;
        if (!IsCanonical(rawStage))
            enriched["stage_normalization"] = "normalized_from_stage";
        enriched["stage_order"] = GetOrder(normalizedStage).ToString("000");
        enriched["stage_label"] = GetLabel(normalizedStage);
        return enriched;
    }

    private static string Resolve(
        string? component,
        string? operation,
        string? category,
        string? name,
        string? status)
    {
        if (string.Equals(status, RuntimeActivityStatuses.Failed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, TelemetryMetricStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            return Error;

        var value = $"{component} {operation} {category} {name}".ToLowerInvariant();
        if (value.Contains(".failed") || value.Contains(" failed"))
            return Error;
        if (value.Contains("ui.") || value.Contains("frontend") || value.Contains("paint"))
            return UiRender;
        if (value.Contains("steering.created"))
            return Dispatch;
        if (value.Contains("steering.injected") || value.Contains("agent.steering.inject") || value.Contains("steering"))
            return LlmPrepare;
        if (value.Contains("token_usage") || value.Contains("token.usage"))
            return Complete;
        if (value.Contains("cache"))
            return Context;
        if (value.Contains("chat.post.received") || value.Contains("message.received"))
            return Request;
        if (value.Contains("chat.route"))
            return Routing;
        if (value.Contains("sub_agent spawn") || value.Contains("subagent.spawn"))
            return Dispatch;
        if (value.Contains("sub_agent complete") || value.Contains("subagent.complete"))
            return Complete;
        if (value.Contains("event_queue enqueue") || value.Contains("agent_execution execute") || value.Contains("agent.hooks.loop_start"))
            return Dispatch;
        if (value.Contains("hook_system") || value.Contains("hook.publish"))
            return Dispatch;
        if (value.Contains("metadata.wait"))
            return Dispatch;
        if (value.Contains("metadata.received"))
            return StreamDeliver;
        if (value.Contains("dispatch") || value.Contains("queued") || value.Contains("background"))
            return Dispatch;
        if (value.Contains("context") || value.Contains("history.hydrate") || value.Contains("memory"))
            return Context;
        if (value.Contains("tool"))
            return Tool;
        if (value.Contains("llm_config") || value.Contains("llm.prepare") || value.Contains("prefix_snapshot") || value.Contains("inject_secrets") || value.Contains("keyvault"))
            return LlmPrepare;
        if (value.Contains("llm") || value.Contains("provider") || value.Contains("chat_stream") || value.Contains("first_chunk"))
            return LlmProvider;
        if (value.Contains("ssm") || value.Contains("append") || value.Contains("persist") || value.Contains("session_state"))
            return StreamPersist;
        if (value.Contains("sse") || value.Contains("stream.") || value.Contains("fanout") || value.Contains("delta"))
            return StreamDeliver;
        if (value.Contains("runtime_control")
            || value.Contains("system_command")
            || value.Contains("handled")
            || value.Contains("completed")
            || value.Contains("done")
            || value.Contains("returned"))
            return Complete;
        return Unknown;
    }
}

public sealed record RuntimeActivity
{
    public string ActivityId { get; init; } = Guid.NewGuid().ToString("N");
    public required RuntimeTraceContext Trace { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string Severity { get; init; } = "info";
    public string? Summary { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record RuntimeActivityQuery
{
    public string? TraceId { get; init; }
    public string? SessionId { get; init; }
    public string? ExecutionId { get; init; }
    public string? Component { get; init; }
    public int Limit { get; init; } = 100;
}

public interface IRuntimeTraceAccessor
{
    RuntimeTraceContext? Current { get; set; }
}

public interface IRuntimeActivitySink
{
    Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default);
    Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default);
}
