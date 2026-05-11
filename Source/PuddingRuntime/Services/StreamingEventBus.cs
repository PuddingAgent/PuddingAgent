using System.Threading.Channels;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// 基于 Channel 的流式事件总线。任意组件可 Emit 事件，
/// 订阅者通过 IAsyncEnumerable 异步消费。
/// 该总线是所有可观测性流式事件的中枢。
/// </summary>
public sealed class StreamingEventBus : IStreamingEventBus
{
    private readonly Channel<StreamingEvent> _channel = Channel.CreateUnbounded<StreamingEvent>();

    public async Task EmitAsync(StreamingEvent ev, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(ev, ct);

    /// <summary>异步消费事件流（供 SseEventForwarder 使用）。</summary>
    public IAsyncEnumerable<StreamingEvent> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
