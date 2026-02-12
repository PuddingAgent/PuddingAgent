using PuddingCode.Platform;
using PuddingPlatform.Services;
using System.Threading.Channels;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Verifies session SSE batching keeps token order while reducing per-frame flush pressure.
/// </summary>
[TestClass]
public sealed class SseFrameBatchPumpTests
{
    [TestMethod]
    public async Task PumpAsync_CoalescesQueuedDeltaFramesIntoOneFlush()
    {
        var channel = Channel.CreateUnbounded<ServerSentEventFrame>();
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, """{"delta":"a"}"""));
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, """{"delta":"b"}"""));
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, """{"delta":"c"}"""));
        channel.Writer.Complete();

        var written = new List<string>();
        var flushes = new List<SseFrameBatchFlush>();
        var pump = new SseFrameBatchPump(new SseFrameBatchPumpOptions
        {
            MaxFlushDelay = TimeSpan.FromMilliseconds(5),
        });

        await pump.PumpAsync(
            channel.Reader,
            (frame, _, _) =>
            {
                written.Add(frame.Data);
                return Task.CompletedTask;
            },
            (flush, _) =>
            {
                flushes.Add(flush);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { """{"delta":"a"}""", """{"delta":"b"}""", """{"delta":"c"}""" },
            written);
        Assert.AreEqual(1, flushes.Count);
        Assert.AreEqual(3, flushes[0].FrameCount);
        Assert.AreEqual(SseEventTypes.Delta, flushes[0].FirstEvent);
        Assert.AreEqual(SseEventTypes.Delta, flushes[0].LastEvent);
        Assert.AreEqual("channel_completed", flushes[0].Reason);
    }

    [TestMethod]
    public async Task PumpAsync_FlushesImmediatelyForTerminalEvent()
    {
        var channel = Channel.CreateUnbounded<ServerSentEventFrame>();
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, """{"delta":"a"}"""));
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Done, """{"messageId":"m1"}"""));
        await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, """{"delta":"late"}"""));
        channel.Writer.Complete();

        var flushes = new List<SseFrameBatchFlush>();
        var pump = new SseFrameBatchPump(new SseFrameBatchPumpOptions
        {
            MaxFlushDelay = TimeSpan.FromMilliseconds(100),
        });

        await pump.PumpAsync(
            channel.Reader,
            (_, _, _) => Task.CompletedTask,
            (flush, _) =>
            {
                flushes.Add(flush);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(2, flushes.Count);
        Assert.AreEqual(2, flushes[0].FrameCount);
        Assert.AreEqual(SseEventTypes.Done, flushes[0].LastEvent);
        Assert.AreEqual("terminal", flushes[0].Reason);
        Assert.AreEqual(1, flushes[1].FrameCount);
    }

    [TestMethod]
    public async Task PumpAsync_FlushesByFrameCountThreshold()
    {
        var channel = Channel.CreateUnbounded<ServerSentEventFrame>();
        for (var i = 0; i < 5; i++)
            await channel.Writer.WriteAsync(new ServerSentEventFrame(SseEventTypes.Delta, $$"""{"delta":"{{i}}"}"""));
        channel.Writer.Complete();

        var flushes = new List<SseFrameBatchFlush>();
        var pump = new SseFrameBatchPump(new SseFrameBatchPumpOptions
        {
            MaxFlushDelay = TimeSpan.FromMilliseconds(100),
            MaxBatchFrames = 2,
        });

        await pump.PumpAsync(
            channel.Reader,
            (_, _, _) => Task.CompletedTask,
            (flush, _) =>
            {
                flushes.Add(flush);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 2, 2, 1 }, flushes.Select(x => x.FrameCount).ToArray());
        Assert.AreEqual("frame_count", flushes[0].Reason);
        Assert.AreEqual("frame_count", flushes[1].Reason);
    }
}
