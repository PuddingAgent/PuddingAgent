using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Mirrors raw session events into the owning agent's private evidence directory.
/// </summary>
public sealed class AgentRawLogMirrorService(
    PuddingDataPaths paths,
    ILogger<AgentRawLogMirrorService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task MirrorAsync(AgentRawLogMirrorRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.AgentInstanceId)
            || string.IsNullOrWhiteSpace(record.SessionId)
            || string.IsNullOrWhiteSpace(record.EventType)
            || string.IsNullOrWhiteSpace(record.RecordedAt))
        {
            return;
        }

        if (!DateTimeOffset.TryParse(record.RecordedAt, out var recordedAt))
        {
            logger.LogWarning(
                "[AgentRawLogMirror] Skip raw mirror because recordedAt is invalid session={Session} agent={Agent} recordedAt={RecordedAt}",
                record.SessionId,
                record.AgentInstanceId,
                record.RecordedAt);
            return;
        }

        var day = recordedAt.UtcDateTime.ToString("yyyy-MM-dd");
        var dayRoot = paths.AgentInstanceRawLogDayRoot(record.AgentInstanceId, day);
        Directory.CreateDirectory(dayRoot);

        var path = paths.AgentInstanceRawLogJsonlFile(record.AgentInstanceId, day, record.SessionId);
        var evidenceRef = $"session-raw:{day}:{record.SessionId}:{record.SequenceNum}";
        var line = JsonSerializer.Serialize(record with { EvidenceRef = evidenceRef }, JsonOptions) + Environment.NewLine;

        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed record AgentRawLogMirrorRecord(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string SessionId,
    long SequenceNum,
    string EventType,
    string Data,
    string RecordedAt,
    string? TraceId,
    string? CorrelationId,
    string? ExecutionId,
    string? ParentExecutionId,
    string? SubAgentId,
    string? Component,
    string? Operation,
    string? EvidenceRef = null);
