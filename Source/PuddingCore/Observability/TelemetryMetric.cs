namespace PuddingCode.Observability;

public static class TelemetryMetricCategories
{
    public const string Session = "session";
    public const string Context = "context";
    public const string Llm = "llm";
    public const string Tool = "tool";
    public const string TokenUsage = "token_usage";
    public const string Cache = "cache";
    public const string Memory = "memory";
}

public static class TelemetryMetricStatuses
{
    public const string Started = "started";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Deferred = "deferred";
    public const string Retried = "retried";
    public const string Recorded = "recorded";
}

/// <summary>
/// 可聚合的遥测事实。与 runtime_activity 的事件流水不同，该结构面向长期统计和 SQL 聚合。
/// </summary>
public sealed record TelemetryMetric
{
    public string MetricId { get; init; } = Guid.NewGuid().ToString("N");
    public required RuntimeTraceContext Trace { get; init; }
    public required string Source { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public long? DurationMs { get; init; }
    public long? CountValue { get; init; }
    public double? NumericValue { get; init; }
    public string? Unit { get; init; }
    public string Severity { get; init; } = "info";
    public string? Summary { get; init; }
    public IReadOnlyDictionary<string, string>? Dimensions { get; init; }
    public string? DebugJson { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ITelemetryMetricSink
{
    Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default);
}
