using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// ISessionEventStream 实现——合并 replay（数据库） + live（head 通知）。
/// <para>
/// ADR-056 正确的 SSE 重连算法：
/// 1. 先获取数据库高水位 H
/// 2. 循环读取并发送 (afterSequence, H]
/// 3. 开始消费 head 通知
/// 4. 收到 committedThrough=N 后，从数据库读取 (lastSent, N]
/// 5. 跳过 sequence <= lastSent
/// 6. 心跳由调用方控制
/// </para>
/// </summary>
public sealed class SessionEventStreamService : ISessionEventStream
{
    private readonly ISessionEventReader _reader;
    private readonly ISessionHeadNotifier _notifier;
    private readonly ILogger<SessionEventStreamService> _logger;

    public SessionEventStreamService(
        ISessionEventReader reader,
        ISessionHeadNotifier notifier,
        ILogger<SessionEventStreamService> logger)
    {
        _reader = reader;
        _notifier = notifier;
        _logger = logger;
    }

    public async IAsyncEnumerable<SessionEventEnvelope> FollowAsync(
        string sessionId,
        long afterExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            yield break;

        // Phase 0: Replay — catch up to current high watermark
        var nextAfter = afterExclusive;
        var head = await _reader.GetHeadAsync(sessionId, ct);

        while (nextAfter < head && !ct.IsCancellationRequested)
        {
            var batch = await _reader.ReadAfterAsync(
                sessionId, nextAfter, throughInclusive: head, limit: 256, ct);

            foreach (var env in batch)
            {
                if (ct.IsCancellationRequested) yield break;
                if (env.Sequence <= afterExclusive) continue;
                yield return env;
                nextAfter = env.Sequence;
            }

            // If we got less than limit, we've caught up (no more events before head)
            if (batch.Count < 256 || (batch.Count > 0 && batch[^1].Sequence >= head))
                break;

            head = await _reader.GetHeadAsync(sessionId, ct);
        }

        // Phase 1: Live — follow head notifications
        await foreach (var notification in _notifier.SubscribeAsync(sessionId, ct))
        {
            if (ct.IsCancellationRequested) break;

            // Read all events between lastSent and committedThrough
            var newHead = notification.CommittedThroughSequence;
            if (newHead <= nextAfter) continue;

            var batch = await _reader.ReadAfterAsync(
                sessionId, nextAfter, throughInclusive: newHead, limit: 256, ct);

            foreach (var env in batch)
            {
                if (ct.IsCancellationRequested) yield break;
                if (env.Sequence <= nextAfter) continue; // dedup
                yield return env;
                nextAfter = env.Sequence;
            }

            // Gap detection: if we didn't reach newHead, there's a gap.
            // Next notification or high-watermark check will close it.
            if (batch.Count > 0 && batch[^1].Sequence < newHead)
            {
                _logger.LogWarning(
                    "[SSES] Gap detected session={Session} lastSent={LastSent} head={Head}",
                    sessionId, nextAfter, newHead);
            }
        }
    }
}
