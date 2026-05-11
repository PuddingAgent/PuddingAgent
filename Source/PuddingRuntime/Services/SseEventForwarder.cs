using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// 将 StreamingEvent 转发为 ServerSentEventFrame，提供 IAsyncEnumerable 供 HTTP SSE 输出。
/// 这是事件总线到前端 SSE 连接的桥梁。
/// </summary>
public sealed class SseEventForwarder
{
    private readonly StreamingEventBus _bus;
    private readonly ILogger<SseEventForwarder> _logger;

    public SseEventForwarder(StreamingEventBus bus, ILogger<SseEventForwarder> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// 订阅事件总线，映射为 SSE 帧流。
    /// 事件类型直接用作 SSE event 名，Data 被序列化为 JSON。
    /// </summary>
    public async IAsyncEnumerable<ServerSentEventFrame> ForwardAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in _bus.ReadAllAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(ev.Type)) continue;

            ServerSentEventFrame? frame = null;
            try
            {
                frame = ServerSentEventFrame.Json(ev.Type, ev.Data ?? new { });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[SseForwarder] Skip malformed event type={Type}", ev.Type);
            }

            if (frame is not null)
                yield return frame;
        }
    }
}
