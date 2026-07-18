// ── P2: Stream Metrics ────────────────────────────────────────
// Lightweight in-memory metrics for SSE stream observability.
// ───────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace PuddingPlatform.Services;

public sealed class StreamMetrics
{
    private int _activeConnections;
    private long _totalReplayEvents;
    private long _totalLiveEvents;
    private long _gapRecoveries;
    private long _reconnectCount;
    private long _duplicateEvents;
    private long _orphanTerminals;
    private readonly ConcurrentDictionary<string, long> _projectionLag = new();

    public int ActiveConnections => _activeConnections;
    public long TotalReplayEvents => _totalReplayEvents;
    public long TotalLiveEvents => _totalLiveEvents;
    public long GapRecoveries => _gapRecoveries;
    public long ReconnectCount => _reconnectCount;
    public long DuplicateEvents => _duplicateEvents;
    public long OrphanTerminals => _orphanTerminals;

    public void RecordConnectionOpen()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    public void RecordConnectionClose()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    public void RecordReplayEvents(long count)
    {
        Interlocked.Add(ref _totalReplayEvents, count);
    }

    public void RecordLiveEvent()
    {
        Interlocked.Increment(ref _totalLiveEvents);
    }

    public void RecordGapRecovery()
    {
        Interlocked.Increment(ref _gapRecoveries);
    }

    public void RecordReconnect()
    {
        Interlocked.Increment(ref _reconnectCount);
    }

    public void RecordDuplicateEvent()
    {
        Interlocked.Increment(ref _duplicateEvents);
    }

    public void RecordOrphanTerminal()
    {
        Interlocked.Increment(ref _orphanTerminals);
    }

    public void RecordProjectionLag(string sessionId, long lag)
    {
        _projectionLag[sessionId] = lag;
    }

    public IReadOnlyDictionary<string, long> GetProjectionLag() =>
        new Dictionary<string, long>(_projectionLag);

    public StreamMetricsSnapshot Snapshot() => new()
    {
        ActiveConnections = _activeConnections,
        TotalReplayEvents = _totalReplayEvents,
        TotalLiveEvents = _totalLiveEvents,
        GapRecoveries = _gapRecoveries,
        ReconnectCount = _reconnectCount,
        DuplicateEvents = _duplicateEvents,
        OrphanTerminals = _orphanTerminals,
        ProjectionLag = new Dictionary<string, long>(_projectionLag),
    };
}

public sealed record StreamMetricsSnapshot
{
    public int ActiveConnections { get; init; }
    public long TotalReplayEvents { get; init; }
    public long TotalLiveEvents { get; init; }
    public long GapRecoveries { get; init; }
    public long ReconnectCount { get; init; }
    public long DuplicateEvents { get; init; }
    public long OrphanTerminals { get; init; }
    public Dictionary<string, long> ProjectionLag { get; init; } = new();
}
