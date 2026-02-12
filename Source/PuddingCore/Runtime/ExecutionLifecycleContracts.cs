using PuddingCode.Observability;

namespace PuddingCode.Runtime;

/// <summary>执行生命周期 Metadata 键名常量，确保 timeline 和诊断兼容。</summary>
public static class LifecycleMetadataKeys
{
    public const string ProfileId = "profile_id";
    public const string ProviderId = "provider_id";
    public const string ModelId = "model_id";
    public const string ContextTokensEstimated = "context_tokens_estimated";
    public const string ContextLayerCount = "context_layer_count";
    public const string ToolName = "tool_name";
    public const string ToolArgsHash = "tool_args_hash";
    public const string SubAgentRunId = "subagent_run_id";
}

/// <summary>执行生命周期记录，作为 Runtime 的统一语言。</summary>
public sealed record ExecutionLifecycleRecord
{
    public required string ExecutionId { get; init; }
    public required string TraceId { get; init; }
    public string? CorrelationId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>执行生命周期记录器，统一投影到 RuntimeActivity。</summary>
public interface IExecutionLifecycleRecorder
{
    /// <summary>启动一个生命周期记录，返回 activityId。</summary>
    Task<string> StartAsync(ExecutionLifecycleRecord record, CancellationToken ct = default);

    /// <summary>完成一个生命周期记录。</summary>
    Task CompleteAsync(string activityId, string status, string? summary = null, string? error = null, CancellationToken ct = default);

    /// <summary>记录一个瞬时事件（无需 start/complete 配对）。</summary>
    Task RecordInstantAsync(ExecutionLifecycleRecord record, CancellationToken ct = default);
}
