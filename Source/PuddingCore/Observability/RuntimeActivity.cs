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
