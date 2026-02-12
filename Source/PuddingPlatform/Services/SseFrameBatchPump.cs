using PuddingCode.Platform;
using System.Diagnostics;
using System.Threading.Channels;

namespace PuddingPlatform.Services;

/// <summary>
/// Pumps session SSE frames in short batches so small token deltas do not force
/// one network flush per frame. The pump preserves frame order and caps added
/// latency with <see cref="SseFrameBatchPumpOptions.MaxFlushDelay"/>.
/// </summary>
public sealed class SseFrameBatchPump
{
    private readonly SseFrameBatchPumpOptions _options;

    public SseFrameBatchPump(SseFrameBatchPumpOptions? options = null)
    {
        _options = options ?? SseFrameBatchPumpOptions.Default;
    }

    /// <summary>
    /// Reads frames from <paramref name="reader"/>, writes them in order, and
    /// flushes after a bounded batch delay, size threshold, or terminal event.
    /// </summary>
    public async Task PumpAsync(
        ChannelReader<ServerSentEventFrame> reader,
        Func<ServerSentEventFrame, long, CancellationToken, Task> writeFrameAsync,
        Func<SseFrameBatchFlush, CancellationToken, Task> flushAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writeFrameAsync);
        ArgumentNullException.ThrowIfNull(flushAsync);

        var frameIndex = 0L;
        while (await reader.WaitToReadAsync(ct))
        {
            if (!reader.TryRead(out var firstFrame))
                continue;

            var batch = SseFrameBatchBuilder.Start(++frameIndex, firstFrame);
            await writeFrameAsync(firstFrame, frameIndex, ct);

            var flushReason = GetImmediateFlushReason(batch);
            if (flushReason is null)
                flushReason = await FillBatchWindowAsync(reader, writeFrameAsync, batch, () => ++frameIndex, ct);

            await flushAsync(batch.ToFlush(flushReason), ct);
        }
    }

    private async Task<string> FillBatchWindowAsync(
        ChannelReader<ServerSentEventFrame> reader,
        Func<ServerSentEventFrame, long, CancellationToken, Task> writeFrameAsync,
        SseFrameBatchBuilder batch,
        Func<long> nextFrameIndex,
        CancellationToken ct)
    {
        var window = Stopwatch.StartNew();
        while (true)
        {
            while (reader.TryRead(out var frame))
            {
                var index = nextFrameIndex();
                batch.Add(index, frame);
                await writeFrameAsync(frame, index, ct);

                var flushReason = GetImmediateFlushReason(batch);
                if (flushReason is not null)
                    return flushReason;
            }

            var remainingMs = _options.MaxFlushDelay.TotalMilliseconds - window.Elapsed.TotalMilliseconds;
            if (remainingMs <= 0)
                return SseFlushReasons.Delay;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(remainingMs), delayCts.Token);
            var waitTask = reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(delayTask, waitTask);
            if (completed == delayTask)
                return SseFlushReasons.Delay;

            await delayCts.CancelAsync();
            if (!await waitTask)
                return SseFlushReasons.ChannelCompleted;
        }
    }

    private string? GetImmediateFlushReason(SseFrameBatchBuilder batch)
    {
        if (IsTerminalEvent(batch.LastEvent))
            return SseFlushReasons.Terminal;
        if (batch.FrameCount >= _options.MaxBatchFrames)
            return SseFlushReasons.FrameCount;
        if (batch.DataChars >= _options.MaxBatchDataChars)
            return SseFlushReasons.Bytes;
        return null;
    }

    private static bool IsTerminalEvent(string eventName) =>
        eventName is
            SseEventTypes.Done or
            SseEventTypes.Error or
            SseEventTypes.Cancelled or
            SessionEventTypes.SessionClosed;
}

/// <summary>Batching thresholds for session SSE output.</summary>
public sealed record SseFrameBatchPumpOptions
{
    public static SseFrameBatchPumpOptions Default { get; } = new();

    /// <summary>Maximum latency added while waiting for adjacent small frames.</summary>
    public TimeSpan MaxFlushDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Maximum frames per batch before a forced flush.</summary>
    public int MaxBatchFrames { get; init; } = 24;

    /// <summary>Maximum serialized data chars per batch before a forced flush.</summary>
    public int MaxBatchDataChars { get; init; } = 4096;
}

/// <summary>Metadata emitted after a batch is flushed to an SSE response.</summary>
public sealed record SseFrameBatchFlush(
    long FirstFrameIndex,
    long LastFrameIndex,
    int FrameCount,
    int DataChars,
    string FirstEvent,
    string LastEvent,
    string Reason);

internal static class SseFlushReasons
{
    public const string Delay = "delay";
    public const string Terminal = "terminal";
    public const string FrameCount = "frame_count";
    public const string Bytes = "bytes";
    public const string ChannelCompleted = "channel_completed";
}

internal sealed class SseFrameBatchBuilder
{
    private SseFrameBatchBuilder(long frameIndex, ServerSentEventFrame frame)
    {
        FirstFrameIndex = frameIndex;
        LastFrameIndex = frameIndex;
        FirstEvent = frame.Event;
        LastEvent = frame.Event;
        FrameCount = 1;
        DataChars = frame.Data.Length;
    }

    public long FirstFrameIndex { get; }
    public long LastFrameIndex { get; private set; }
    public int FrameCount { get; private set; }
    public int DataChars { get; private set; }
    public string FirstEvent { get; }
    public string LastEvent { get; private set; }

    public static SseFrameBatchBuilder Start(long frameIndex, ServerSentEventFrame frame) =>
        new(frameIndex, frame);

    public void Add(long frameIndex, ServerSentEventFrame frame)
    {
        LastFrameIndex = frameIndex;
        LastEvent = frame.Event;
        FrameCount++;
        DataChars += frame.Data.Length;
    }

    public SseFrameBatchFlush ToFlush(string reason) =>
        new(FirstFrameIndex, LastFrameIndex, FrameCount, DataChars, FirstEvent, LastEvent, reason);
}
