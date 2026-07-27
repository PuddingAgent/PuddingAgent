using System.Text.Json;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Thread-safe aggregation of hot-path streaming diagnostics.
/// It deliberately owns aggregation only; persistence remains an execution lifecycle concern.
/// </summary>
internal sealed class StreamPipelineDiagnosticsAccumulator
{
    private long _keyVaultCount;
    private long _keyVaultTotalMs;
    private long _keyVaultMaxMs;
    private long _keyVaultInputChars;
    private long _keyVaultDeltaCount;
    private long _keyVaultFinalCount;
    private long _ssmAppendCount;
    private long _ssmAppendTotalMs;
    private long _ssmAppendMaxMs;
    private long _ssmAppendDataChars;
    private long _ssmDeltaAppendCount;
    private long _ssmThinkingAppendCount;

    public bool IsEmpty => _keyVaultCount == 0 && _ssmAppendCount == 0;

    public void ObserveKeyVaultStrip(long durationMs, string stage, int inputChars)
    {
        Interlocked.Increment(ref _keyVaultCount);
        Interlocked.Add(ref _keyVaultTotalMs, durationMs);
        UpdateMax(ref _keyVaultMaxMs, durationMs);
        Interlocked.Add(ref _keyVaultInputChars, inputChars);
        if (string.Equals(stage, "delta", StringComparison.Ordinal))
            Interlocked.Increment(ref _keyVaultDeltaCount);
        else
            Interlocked.Increment(ref _keyVaultFinalCount);
    }

    public void ObserveSsmAppend(long durationMs, string eventType, int dataChars)
    {
        Interlocked.Increment(ref _ssmAppendCount);
        Interlocked.Add(ref _ssmAppendTotalMs, durationMs);
        UpdateMax(ref _ssmAppendMaxMs, durationMs);
        Interlocked.Add(ref _ssmAppendDataChars, dataChars);
        if (string.Equals(eventType, SseEventTypes.Delta, StringComparison.Ordinal))
            Interlocked.Increment(ref _ssmDeltaAppendCount);
        else if (string.Equals(eventType, SseEventTypes.Thinking, StringComparison.Ordinal))
            Interlocked.Increment(ref _ssmThinkingAppendCount);
    }

    public IReadOnlyList<TelemetryMetric> ToMetrics(RuntimeTraceContext trace, string status)
    {
        var keyVaultCount = Interlocked.Read(ref _keyVaultCount);
        var keyVaultTotalMs = Interlocked.Read(ref _keyVaultTotalMs);
        var keyVaultMaxMs = Interlocked.Read(ref _keyVaultMaxMs);
        var ssmAppendCount = Interlocked.Read(ref _ssmAppendCount);
        var ssmAppendTotalMs = Interlocked.Read(ref _ssmAppendTotalMs);
        var ssmAppendMaxMs = Interlocked.Read(ref _ssmAppendMaxMs);

        var metrics = new List<TelemetryMetric>(2);
        if (keyVaultCount > 0)
        {
            var dimensions = new Dictionary<string, string>
            {
                ["operation"] = "keyvault.strip",
                ["input_chars"] = Interlocked.Read(ref _keyVaultInputChars).ToString(),
                ["delta_count"] = Interlocked.Read(ref _keyVaultDeltaCount).ToString(),
                ["final_count"] = Interlocked.Read(ref _keyVaultFinalCount).ToString(),
                ["avg_ms"] = Average(keyVaultTotalMs, keyVaultCount).ToString("0.###"),
                ["max_ms"] = keyVaultMaxMs.ToString(),
            };
            metrics.Add(BuildMetric(
                trace,
                "agent.stream.keyvault_strip",
                status,
                keyVaultCount,
                Average(keyVaultTotalMs, keyVaultCount),
                keyVaultMaxMs,
                "KeyVault strip latency in streaming output.",
                dimensions));
        }

        if (ssmAppendCount > 0)
        {
            var dimensions = new Dictionary<string, string>
            {
                ["operation"] = "ssm.append",
                ["data_chars"] = Interlocked.Read(ref _ssmAppendDataChars).ToString(),
                ["delta_count"] = Interlocked.Read(ref _ssmDeltaAppendCount).ToString(),
                ["thinking_count"] = Interlocked.Read(ref _ssmThinkingAppendCount).ToString(),
                ["avg_ms"] = Average(ssmAppendTotalMs, ssmAppendCount).ToString("0.###"),
                ["max_ms"] = ssmAppendMaxMs.ToString(),
            };
            metrics.Add(BuildMetric(
                trace,
                "agent.stream.ssm_append",
                status,
                ssmAppendCount,
                Average(ssmAppendTotalMs, ssmAppendCount),
                ssmAppendMaxMs,
                "Session state append latency in streaming output.",
                dimensions));
        }

        return metrics;
    }

    private static TelemetryMetric BuildMetric(
        RuntimeTraceContext trace,
        string name,
        string status,
        long count,
        double averageMs,
        long maxMs,
        string summary,
        IReadOnlyDictionary<string, string> dimensions)
        => new()
        {
            Trace = trace,
            Source = "backend",
            Category = TelemetryMetricCategories.Session,
            Name = name,
            Status = status,
            CountValue = count,
            NumericValue = averageMs,
            DurationMs = maxMs,
            Unit = "ms",
            Summary = summary,
            Dimensions = dimensions,
            DebugJson = JsonSerializer.Serialize(dimensions),
        };

    private static double Average(long total, long count)
        => count <= 0 ? 0 : (double)total / count;

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
