using System.Collections.Concurrent;
using System.Threading.Channels;
using PuddingCode.Platform;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-057: ICommittedEventSignal 实现。
/// 基于 Channel 的广播通知。Signal() 写入 head 到对应 conversation 的 Channel。
/// WaitForChangeAsync() 订阅并等待 head 超过 knownHead。
/// </summary>
public sealed class CommittedEventSignal : ICommittedEventSignal
{
    private readonly ConcurrentDictionary<string, Channel<long>> _channels = new();

    public ValueTask WaitForChangeAsync(
        string conversationId,
        long knownHead,
        CancellationToken ct)
    {
        var channel = _channels.GetOrAdd(conversationId, _ => Channel.CreateBounded<long>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            }));

        return WaitLoopAsync(channel.Reader, knownHead, ct);
    }

    private static async ValueTask WaitLoopAsync(
        ChannelReader<long> reader,
        long knownHead,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var head = await reader.ReadAsync(ct);
                if (head > knownHead) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ChannelClosedException) { return; }
        }
    }

    public void Signal(string conversationId, long committedThroughSequence)
    {
        if (_channels.TryGetValue(conversationId, out var channel))
        {
            channel.Writer.TryWrite(committedThroughSequence);
        }
    }
}
