using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 协同总线接口——锁生命周期事件的发布/订阅总线。
/// 用于让 CLI 状态面板、Agent 间通知和审计跟踪感知锁变化。
/// </summary>
public interface ICoordinationEventBus
{
    /// <summary>发布一个协同事件到总线。</summary>
    void Publish(CoordinationEvent evt);

    /// <summary>
    /// 订阅总线事件。
    /// 返回的 <see cref="CoordinationEventSubscription"/> Dispose 后取消订阅。
    /// </summary>
    CoordinationEventSubscription Subscribe(Action<CoordinationEvent> handler);

    /// <summary>获取最近若干条事件快照（用于面板初始渲染）。</summary>
    IReadOnlyList<CoordinationEvent> GetRecent(int count = 20);
}
