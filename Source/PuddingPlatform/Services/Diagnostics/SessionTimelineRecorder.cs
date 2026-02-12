using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Observability;

namespace PuddingPlatform.Services.Diagnostics;

public sealed record SessionTimelineRecorderOptions
{
    public bool Enabled { get; init; }
}

public sealed record SessionTimelineRecord
{
    public required RuntimeTraceContext Trace { get; init; }
    public required string Component { get; init; }
    public required string Stage { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string Severity { get; init; } = "info";
    public string? Summary { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ISessionTimelineRecorder
{
    Task RecordAsync(SessionTimelineRecord record, CancellationToken ct = default);
}

/// <summary>
/// Writes machine-readable diagnostic timeline events without mixing them into
/// ordinary text logs. Events are mirrored to runtime_activity and per-session
/// JSONL files under data/logs/diagnostics.
/// </summary>
public sealed class SessionTimelineRecorder : ISessionTimelineRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly PuddingDataPaths _paths;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly SessionTimelineRecorderOptions _options;

    public SessionTimelineRecorder(
        PuddingDataPaths paths,
        IRuntimeActivitySink activitySink,
        SessionTimelineRecorderOptions options)
    {
        _paths = paths;
        _activitySink = activitySink;
        _options = options;
    }

    public async Task RecordAsync(SessionTimelineRecord record, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return;

        var metadata = NormalizeMetadata(record.Metadata, record.Stage);
        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = record.Trace,
            Component = record.Component,
            Operation = record.Operation,
            Status = record.Status,
            StartedAtUtc = record.RecordedAtUtc,
            EndedAtUtc = record.CompletedAtUtc,
            DurationMs = record.DurationMs,
            Severity = record.Severity,
            Summary = record.Summary,
            Metadata = metadata,
            ErrorCode = record.ErrorCode,
            ErrorMessage = record.ErrorMessage,
        }, ct);

        var line = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            recordKind = "trace",
            recordedAtUtc = record.RecordedAtUtc.ToString("O"),
            completedAtUtc = record.CompletedAtUtc?.ToString("O"),
            durationMs = record.DurationMs,
            traceId = record.Trace.TraceId,
            correlationId = record.Trace.CorrelationId,
            sessionId = record.Trace.SessionId,
            workspaceId = record.Trace.WorkspaceId,
            executionId = record.Trace.ExecutionId,
            parentExecutionId = record.Trace.ParentExecutionId,
            subAgentId = record.Trace.SubAgentId,
            eventId = record.Trace.EventId,
            connectorId = record.Trace.ConnectorId,
            userId = record.Trace.UserId,
            component = record.Component,
            stage = metadata.TryGetValue("stage", out var normalizedStage)
                ? normalizedStage
                : record.Stage,
            operation = record.Operation,
            status = record.Status,
            severity = record.Severity,
            summary = record.Summary,
            metadata,
            errorCode = record.ErrorCode,
            errorMessage = record.ErrorMessage,
        }, JsonOptions);

        var file = GetTimelineFile(record);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(file, line + Environment.NewLine, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string GetTimelineFile(SessionTimelineRecord record)
    {
        var sessionId = string.IsNullOrWhiteSpace(record.Trace.SessionId)
            ? "no-session"
            : record.Trace.SessionId;
        var fileName = SanitizeFileName(sessionId) + ".jsonl";
        return Path.Combine(
            _paths.DiagnosticsLogsRoot,
            "session-timeline",
            record.RecordedAtUtc.ToString("yyyyMMdd"),
            fileName);
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string stage)
    {
        var normalized = RuntimePipelineStages.Enrich(metadata, stage);
        return normalized;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
