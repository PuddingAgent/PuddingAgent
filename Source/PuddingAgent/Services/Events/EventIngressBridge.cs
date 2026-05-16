using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingAgent.Services.Events;

/// <summary>
/// 事件入站桥接 — 订阅 IInternalEventBus，将发布的 InternalEvent
/// 通过预处理 + 优先队列送入事件系统管道。
/// 
/// 这是事件系统与外界的「入口」— 任何发布到 IInternalEventBus 的事件
/// （来自 Cron、Webhook Connector、P2P、System 等）都经过此桥进入
/// 预处理→优先级队列→EventDispatcher→IEventHandler 的流水线。
/// 
/// 不关心事件来源 — 只要调用了 IInternalEventBus.PublishAsync() 就会进入。
/// </summary>
public class EventIngressBridge : IHostedService
{
    private readonly IInternalEventBus _eventBus;
    private readonly IEventPreprocessor _preprocessor;
    private readonly IPriorityEventQueue _queue;
    private readonly ILogger<EventIngressBridge> _logger;
    private IEventSubscriptionHandle? _subscription;

    public EventIngressBridge(
        IInternalEventBus eventBus,
        IEventPreprocessor preprocessor,
        IPriorityEventQueue queue,
        ILogger<EventIngressBridge> logger)
    {
        _eventBus = eventBus;
        _preprocessor = preprocessor;
        _queue = queue;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // 订阅所有事件类型（通配符 *）
        _subscription = await _eventBus.SubscribeAsync("*", OnEventAsync, ct);
        _logger.LogInformation("[EventIngressBridge] Subscribed to IInternalEventBus, feeding into pipeline");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        _logger.LogInformation("[EventIngressBridge] Unsubscribed");
        return Task.CompletedTask;
    }

    private async Task OnEventAsync(InternalEvent evt)
    {
        try
        {
            // 将 InternalEvent 包装为 RawEvent 送入预处理
            var raw = new RawEvent
            {
                RawEventId = evt.EventId,
                Type = evt.Type,
                Source = evt.Source,
                WorkspaceId = evt.WorkspaceId,
                AgentId = evt.AgentId,
                SessionId = evt.SessionId,
                Payload = evt.Payload,
                Timestamp = evt.Timestamp,
                Metadata = evt.Metadata,
            };

            var processed = await _preprocessor.ProcessAsync([raw]);

            foreach (var pe in processed)
            {
                await _queue.EnqueueAsync(pe, evt.Priority);
            }

            _logger.LogDebug(
                "[EventIngressBridge] Routed to queue: id={Id} type={Type} pri={Pri}",
                evt.EventId, evt.Type, evt.Priority);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[EventIngressBridge] Failed to route event id={Id} type={Type}",
                evt.EventId, evt.Type);
        }
    }
}
