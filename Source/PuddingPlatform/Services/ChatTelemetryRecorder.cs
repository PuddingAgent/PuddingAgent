using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Services;

/// <summary>
/// Wraps timeline and telemetry recording behind SafeRecorder for the Chat API.
/// </summary>
public sealed class ChatTelemetryRecorder
{
    private readonly ISessionTimelineRecorder _timeline;
    private readonly ITelemetryMetricSink _telemetry;
    private readonly ILogger<ChatTelemetryRecorder> _logger;

    public ChatTelemetryRecorder(
        ISessionTimelineRecorder timeline,
        ITelemetryMetricSink telemetry,
        ILogger<ChatTelemetryRecorder> logger)
    {
        _timeline = timeline;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task RecordTimelineAsync(
        RuntimeTraceContext trace,
        string component,
        string stage,
        string operation,
        string status,
        long? durationMs = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await SafeRecorder.RunAsync(
            ct2 => _timeline.RecordAsync(new SessionTimelineRecord
            {
                Trace = trace,
                Component = component,
                Stage = stage,
                Operation = operation,
                Status = status,
                DurationMs = durationMs,
                Metadata = metadata,
                ErrorMessage = errorMessage,
            }, ct2),
            _logger,
            $"timeline:{stage}",
            ct);
    }

    public async Task RecordTelemetryMetricAsync(
        RuntimeTraceContext trace,
        string category,
        string name,
        string status,
        long? durationMs,
        long? countValue,
        IReadOnlyDictionary<string, string>? dimensions = null,
        DateTimeOffset? occurredAtUtc = null,
        Exception? error = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await SafeRecorder.RunAsync(
            ct2 => _telemetry.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "backend",
                Category = category,
                Name = name,
                Status = status,
                OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
                DurationMs = durationMs,
                CountValue = countValue,
                Unit = countValue is null ? null : "event",
                Severity = error is null && status != TelemetryMetricStatuses.Failed ? "info" : "error",
                Summary = name,
                Dimensions = dimensions,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message ?? errorMessage,
            }, ct2),
            _logger,
            $"telemetry:{name}",
            ct);
    }
}
