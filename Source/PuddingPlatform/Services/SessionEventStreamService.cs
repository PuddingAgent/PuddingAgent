using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// ISessionEventStream 实现——合并 replay（Event Store） + live（ICommittedEventSignal）。
/// ADR-057 Phase 4: subscribe-first 算法 + heartbeat 独立定时器 + gap recovery + snapshot_required。
/// </summary>
public sealed class SessionEventStreamService : ISessionEventStream
{
    private readonly IConversationEventStore _eventStore;
    private readonly ICommittedEventSignal _signal;
    private readonly ILogger<SessionEventStreamService> _logger;
    private readonly StreamMetrics _metrics;

    public SessionEventStreamService(
        IConversationEventStore eventStore,
        ICommittedEventSignal signal,
        ILogger<SessionEventStreamService> logger,
        StreamMetrics metrics)
    {
        _eventStore = eventStore;
        _signal = signal;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// 检查 cursor 是否低于最小可恢复 sequence。
    /// 如果是，返回 { minSeq, snapshotUrl }，调用方应返回 410。
    /// </summary>
    public async Task<SnapshotRequiredInfo?> CheckSnapshotRequiredAsync(
        string sessionId, long cursor, CancellationToken ct)
    {
        var bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
        if (bounds.MinSequence.HasValue && cursor < bounds.MinSequence.Value)
        {
            return new SnapshotRequiredInfo(
                bounds.MinSequence.Value,
                $"/api/conversations/{sessionId}/bootstrap");
        }
        return null;
    }

    public async IAsyncEnumerable<SessionEventEnvelope> FollowAsync(
        string sessionId,
        long afterExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            yield break;

        _metrics.RecordConnectionOpen();

        // Phase 1: Read current head and replay from Event Store.
        var nextAfter = afterExclusive;
        var bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
        var head = bounds.MaxSequence ?? 0;
        var replayCount = 0L;

        while (nextAfter < head && !ct.IsCancellationRequested)
        {
            var batch = await _eventStore.ReadForwardAsync(
                sessionId, nextAfter, throughInclusive: head, limit: 256, ct);

            if (batch.Events.Count == 0) break;

            foreach (var evt in batch.Events)
            {
                if (ct.IsCancellationRequested) yield break;
                if (evt.Sequence <= nextAfter) { _metrics.RecordDuplicateEvent(); continue; }
                yield return ToEnvelope(evt);
                nextAfter = evt.Sequence;
                replayCount++;
            }

            if (batch.Events.Count < 256) break;
            bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
            head = bounds.MaxSequence ?? 0;
        }

        _metrics.RecordReplayEvents(replayCount);

        // Phase 2: Live — notification-driven reads + periodic head poll + inline heartbeat.
        var lastHeartbeat = DateTimeOffset.UtcNow;
        var heartbeatInterval = TimeSpan.FromSeconds(15);
        var pollInterval = TimeSpan.FromSeconds(1);

        // Start notification waiter
        var notificationTask = WaitForNotificationAsync(sessionId, nextAfter, ct);

        while (!ct.IsCancellationRequested)
        {
            var delayTask = Task.Delay(pollInterval, ct);
            var completed = await Task.WhenAny(notificationTask, delayTask);
            if (ct.IsCancellationRequested) break;

            if (completed == notificationTask)
            {
                // Notification received or completed (possibly from cancellation)
                bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
                var newHead = bounds.MaxSequence ?? 0;

                if (newHead > nextAfter)
                {
                    while (nextAfter < newHead && !ct.IsCancellationRequested)
                    {
                        var batch = await _eventStore.ReadForwardAsync(
                            sessionId, nextAfter, throughInclusive: newHead, limit: 256, ct);

                        if (batch.Events.Count == 0) break;

                        foreach (var evt in batch.Events)
                        {
                            if (ct.IsCancellationRequested) yield break;
                            if (evt.Sequence <= nextAfter) continue;
                            yield return ToEnvelope(evt);
                            nextAfter = evt.Sequence;
                            _metrics.RecordLiveEvent();
                        }

                        if (batch.Events.Count < 256) break;
                        bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
                        newHead = bounds.MaxSequence ?? 0;
                    }
                }

                // Re-subscribe
                notificationTask = WaitForNotificationAsync(sessionId, nextAfter, ct);
                lastHeartbeat = DateTimeOffset.UtcNow;
            }
            else
            {
                // Poll timeout — check for new events (in case notification was dropped) and heartbeat
                bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
                var pollHead = bounds.MaxSequence ?? 0;

                if (pollHead > nextAfter)
                {
                    // Events available but notification didn't fire — catch up
                    var batch = await _eventStore.ReadForwardAsync(
                        sessionId, nextAfter, throughInclusive: pollHead, limit: 256, ct);

                    foreach (var evt in batch.Events)
                    {
                        if (ct.IsCancellationRequested) yield break;
                        if (evt.Sequence <= nextAfter) continue;
                        yield return ToEnvelope(evt);
                        nextAfter = evt.Sequence;
                    }

                    // Re-subscribe since we consumed events
                    notificationTask = WaitForNotificationAsync(sessionId, nextAfter, ct);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - lastHeartbeat > heartbeatInterval)
                {
                    yield return HeartbeatEnvelope(sessionId, nextAfter);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    private async Task WaitForNotificationAsync(string sessionId, long knownHead, CancellationToken ct)
    {
        try
        {
            await _signal.WaitForChangeAsync(sessionId, knownHead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static SessionEventEnvelope ToEnvelope(ConversationEvent evt) =>
        new(
            EventId: evt.EventId,
            SessionId: evt.ConversationId,
            ConversationId: evt.ConversationId,
            Sequence: evt.Sequence,
            EventType: evt.Type,
            SchemaVersion: evt.SchemaVersion,
            CommandId: evt.CommandId,
            TurnId: evt.TurnId,
            MessageId: evt.MessageId,
            AgentId: null,
            OccurredAt: evt.OccurredAt,
            Payload: evt.Payload,
            Trace: null
        );

    private static SessionEventEnvelope HeartbeatEnvelope(string sessionId, long seq)
        => new(
            EventId: "_heartbeat_",
            SessionId: sessionId,
            ConversationId: sessionId,
            Sequence: -1,
            EventType: "heartbeat",
            SchemaVersion: 1,
            CommandId: null,
            TurnId: null,
            MessageId: null,
            AgentId: null,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: System.Text.Json.JsonDocument.Parse("{}").RootElement,
            Trace: null
        );
}

/// <summary>
/// Cursor 过期恢复信息。
/// </summary>
public sealed record SnapshotRequiredInfo(
    long MinimumAvailableSequence,
    string SnapshotUrl
);

