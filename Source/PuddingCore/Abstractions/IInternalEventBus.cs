using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 内部事件总线 — 进程内 pub/sub，支持事件类型过滤。
/// 解耦 Cron、Connector、Agent 生命周期等事件发布与消费。
/// </summary>
public interface IInternalEventBus
{
    /// <summary>
    /// 发布事件到所有匹配的订阅者。
    /// </summary>
    Task PublishAsync(InternalEvent evt, CancellationToken ct = default);

    /// <summary>
    /// 订阅特定事件类型。支持通配符模式，如 "cron.*" 或 "mqtt.sensor.*"。
    /// 返回可释放的订阅句柄，Dispose 后自动取消订阅。
    /// </summary>
    Task<IEventSubscriptionHandle> SubscribeAsync(
        string eventTypePattern,
        Func<InternalEvent, Task> handler,
        CancellationToken ct = default);

    /// <summary>
    /// 取消订阅。
    /// </summary>
    Task UnsubscribeAsync(IEventSubscriptionHandle handle);
}

/// <summary>
/// 事件订阅句柄 — Dispose 后自动取消订阅。
/// </summary>
public interface IEventSubscriptionHandle : IDisposable
{
    string SubscriptionId { get; }
    string EventTypePattern { get; }
    bool IsActive { get; }
}
