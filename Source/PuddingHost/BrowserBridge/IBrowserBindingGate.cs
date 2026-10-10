using Pudding.Contracts.Desktop;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 控制面（§4.1）的**接线缝**：Core 在为目标页驱动某个浏览器命令之前，必须确保该 (任务, 页面) 已取得授权。
///
/// 为什么需要缝而不是直接依赖生产者：Runtime 这一层只应知道"**发命令前要过一次门**"这一事实；
/// 由谁签发（`DesktopBrowserBindingProducer`）、是否已签发、被拒如何处置，都属于组合根与策略层的决定。
/// **未注入 ⇒ 保持今天的行为**（组合根决定何时开始接线），因此本缝本身不改变任何语义。
/// </summary>
public interface IBrowserBindingGate
{
    /// <summary>
    /// 确保 (caller, task, context, page) 已获授权；**拒绝必须抛异常**（fail-closed），不得静默放行。
    /// </summary>
    Task EnsureAsync(
        string callerId,
        string taskId,
        string? contextId,
        string pageId,
        CancellationToken cancellationToken);
}