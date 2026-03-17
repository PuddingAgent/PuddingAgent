using System.Collections.Concurrent;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// 内存协同总线——线程安全的同步 pub/sub，适用于单进程 CLI 场景。
/// 事件发布是同步的（在入队线程中依次调用订阅者）；如需异步化可按需扩展。
/// </summary>
public sealed class InMemoryCoordinationEventBus : ICoordinationEventBus
{
    private readonly object _gate = new();
    private readonly List<Action<CoordinationEvent>> _subscribers = [];
    private readonly ConcurrentQueue<CoordinationEvent> _recent = new();
    private const int MaxRecent = 100;

    public void Publish(CoordinationEvent evt)
    {
        // 追加到快照队列
        _recent.Enqueue(evt);
        while (_recent.Count > MaxRecent)
            _recent.TryDequeue(out _);

        // 通知所有订阅者
        Action<CoordinationEvent>[] snapshot;
        lock (_gate)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var handler in snapshot)
        {
            try { handler(evt); }
            catch { /* 订阅者异常不应阻止其他订阅者 */ }
        }
    }

    public CoordinationEventSubscription Subscribe(Action<CoordinationEvent> handler)
    {
        lock (_gate)
        {
            _subscribers.Add(handler);
        }
        return new CoordinationEventSubscription(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(handler);
            }
        });
    }

    public IReadOnlyList<CoordinationEvent> GetRecent(int count = 20)
        => _recent.TakeLast(count).ToList();
}
