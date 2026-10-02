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
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _heartbeatInterval;

    /// <summary>Durable-poll cadence of the live phase (ADR-057: the shared channel may drop a notification).</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Heartbeat cadence of the live phase.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);

    public SessionEventStreamService(
        IConversationEventStore eventStore,
        ICommittedEventSignal signal,
        ILogger<SessionEventStreamService> logger,
        StreamMetrics metrics,
        TimeSpan? pollInterval = null,
        TimeSpan? heartbeatInterval = null)
    {
        _eventStore = eventStore;
        _signal = signal;
        _logger = logger;
        _metrics = metrics;
        // Deliberately injectable so the live phase (durable poll, heartbeat, waiter replacement) is testable
        // without a 15-second test; production keeps the defaults above.
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
    }

    /// <summary>
    /// 检查客户端游标之后是否有确实缺失的事件（判据见 <see cref="SnapshotRequiredCheck"/>）。
    /// 如果是，返回 { minSeq, snapshotUrl }，调用方应返回 410。
    /// </summary>
    public async Task<SnapshotRequiredInfo?> CheckSnapshotRequiredAsync(
        string sessionId, long cursor, CancellationToken ct)
    {
        var bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
        if (SnapshotRequiredCheck.HasMissingEvents(cursor, bounds.MinSequence))
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

        // Phase 1: subscribe BEFORE reading the head. A commit that happens while this connection is being
        // set up then has a waiter already listening, instead of depending on one notification that may have
        // been broadcast (and consumed elsewhere) before the waiter existed.
        var (notificationCts, notificationTask) = StartNotificationWaiter(sessionId, afterExclusive, ct);

        try
        {
            var nextAfter = afterExclusive;

            // The replay's upper bound is read ONCE and frozen. Re-reading it after every 256-event batch is
            // what made a reconnect read the whole conversation index over and over: the frozen bound plus the
            // live phase below still delivers everything, but each batch no longer re-walks the session.
            var bounds = await _eventStore.GetBoundsAsync(sessionId, ct);
            var replayThrough = bounds.MaxSequence ?? 0;
            var replayCount = 0L;

            while (nextAfter < replayThrough && !ct.IsCancellationRequested)
            {
                var batch = await _eventStore.ReadForwardAsync(
                    sessionId, nextAfter, throughInclusive: replayThrough, limit: 256, ct);

                if (batch.Events.Count == 0) break;

                foreach (var evt in batch.Events)
                {
                    if (ct.IsCancellationRequested) yield break;
                    if (evt.Sequence <= nextAfter) { _metrics.RecordDuplicateEvent(); continue; }
                    // Phase 1 = 连接建立时的历史追赶：帧上显式标记 replay，消费端据此区分
                    // 「历史事件」与「此刻发生的事件」（两者原本在帧上不可区分）。
                    yield return ToEnvelope(evt) with { IsReplay = true };
                    nextAfter = evt.Sequence;
                    replayCount++;
                }

                // A short batch is the end of this replay; the frozen bound is never refreshed here — events
                // appended while replaying belong to the live phase, which is entered right below.
                if (batch.Events.Count < 256) break;
            }

            _metrics.RecordReplayEvents(replayCount);

            // Phase 2: Live — notification-driven reads + periodic head poll + inline heartbeat.
            // The durable poll stays: CommittedEventSignal hands each broadcast to a single reader of a shared
            // channel, so no subscriber may assume it receives every notification.
            var lastHeartbeat = DateTimeOffset.UtcNow;
            var heartbeatInterval = _heartbeatInterval;
            var pollInterval = _pollInterval;

            while (!ct.IsCancellationRequested)
            {
                var delayTask = Task.Delay(pollInterval, ct);
                var completed = await Task.WhenAny(notificationTask, delayTask);
                if (ct.IsCancellationRequested) break;

                if (completed == notificationTask)
                {
                    // Freeze this drain's upper bound too: events appended after it are picked up by the next
                    // notification or by the poll below, instead of extending this loop's head forever.
                    var newHead = (await _eventStore.GetBoundsAsync(sessionId, ct)).MaxSequence ?? 0;
                    var drainThrough = newHead;

                    while (nextAfter < drainThrough && !ct.IsCancellationRequested)
                    {
                        var batch = await _eventStore.ReadForwardAsync(
                            sessionId, nextAfter, throughInclusive: drainThrough, limit: 256, ct);

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
                    }

                    // Re-subscribe: the completed waiter is cancelled and awaited first, so it cannot keep
                    // consuming notifications this new waiter is waiting for.
                    (notificationCts, notificationTask) = await ReplaceNotificationWaiterAsync(
                        sessionId, nextAfter, ct, notificationCts, notificationTask).ConfigureAwait(false);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
                else
                {
                    // Poll timeout — check for new events (in case notification was dropped) and heartbeat
                    var pollHead = (await _eventStore.GetBoundsAsync(sessionId, ct)).MaxSequence ?? 0;

                    if (pollHead > nextAfter)
                    {
                        // Events available but notification didn't fire — catch up, bounded by the head read
                        // above, then re-subscribe since we consumed events.
                        var drainThrough = pollHead;

                        while (nextAfter < drainThrough && !ct.IsCancellationRequested)
                        {
                            var batch = await _eventStore.ReadForwardAsync(
                                sessionId, nextAfter, throughInclusive: drainThrough, limit: 256, ct);

                            if (batch.Events.Count == 0) break;

                            foreach (var evt in batch.Events)
                            {
                                if (ct.IsCancellationRequested) yield break;
                                if (evt.Sequence <= nextAfter) continue;
                                yield return ToEnvelope(evt);
                                nextAfter = evt.Sequence;
                            }

                            if (batch.Events.Count < 256) break;
                        }

                        (notificationCts, notificationTask) = await ReplaceNotificationWaiterAsync(
                            sessionId, nextAfter, ct, notificationCts, notificationTask).ConfigureAwait(false);
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
        finally
        {
            // Client disconnect / shutdown: release this wait cycle so no waiter outlives the connection.
            try
            {
                notificationCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already replaced and disposed
            }

            notificationCts.Dispose();
        }
    }

    /// <summary>Starts one wait cycle: its own linked CTS plus the waiter task using that CTS's token.</summary>
    private (CancellationTokenSource Cts, Task Task) StartNotificationWaiter(
        string sessionId,
        long knownHead,
        CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return (cts, WaitForNotificationAsync(sessionId, knownHead, cts.Token));
    }

    /// <summary>
    /// Replaces the wait cycle of this connection: the previous waiter is cancelled and awaited before the new
    /// one subscribes, so a stale waiter can no longer steal a broadcast (a shared channel hands each value to
    /// exactly one reader, so a dropped-but-running waiter would consume the notification the new one needs).
    /// </summary>
    private async Task<(CancellationTokenSource Cts, Task Task)> ReplaceNotificationWaiterAsync(
        string sessionId,
        long knownHead,
        CancellationToken ct,
        CancellationTokenSource previousCts,
        Task previousTask)
    {
        previousCts.Cancel();

        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the wait cycle was replaced. Cancellation is not a successful preparation.
        }
        finally
        {
            previousCts.Dispose();
        }

        return StartNotificationWaiter(sessionId, knownHead, ct);
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
            RunId: evt.RunId,
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
            RunId: null,
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

/// <summary>
/// SSE 订阅起点解析结果。
///
/// 背景（结构性缺陷「实时通道失去时效语义」S4）：
/// 无游标连接此前被当作 <c>after = 0</c>，于是把**多天前**的事件当实时帧下发，
/// 消费端无法区分「历史」与「此刻发生」（帧虽已带 replay 标记，但回放量本身无界）。
///
/// 现在的语义：
/// <list type="bullet">
/// <item>显式游标（含 0）＝客户端声明了自己的权威位置，按该位置回放。
/// 显式 0 表示**有意全量回放**，仅由「刚创建的新会话」使用。</item>
/// <item>无游标＝客户端没有权威位置，fail-safe 只推实时帧（从 head 起），
/// 历史由 <c>/bootstrap</c> 快照负责。未知调用方再也不会拿到伪实时历史。</item>
/// </list>
/// </summary>
public readonly record struct SessionEventStreamStart(long After, string Phase)
{
    public const string LiveOnlyPhase = "live-only";
    public const string ReplayFromZeroPhase = "replay-from-zero";
    public const string ReplayAfterPhase = "replay-after";

    /// <summary>
    /// 解析订阅起点。纯函数，便于在不起 SSE 连接的前提下锁定语义。
    /// </summary>
    public static SessionEventStreamStart Resolve(long? explicitCursor, long head) =>
        explicitCursor is null
            ? new SessionEventStreamStart(head, LiveOnlyPhase)
            : new SessionEventStreamStart(
                Math.Max(0, explicitCursor.Value),
                explicitCursor.Value > 0 ? ReplayAfterPhase : ReplayFromZeroPhase);
}
