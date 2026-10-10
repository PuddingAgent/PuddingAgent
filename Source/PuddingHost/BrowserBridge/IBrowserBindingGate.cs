namespace PuddingHost.BrowserBridge;

using Pudding.Contracts.Desktop;

/// <summary>
/// 控制面（§4.1）的接线缝：Core 在为目标页驱动某个浏览器命令之前，必须确保该 (任务, 页面) 已取得授权。
/// 未注入 ⇒ 保持今天的行为（组合根决定何时开始接线）。
/// </summary>
public interface IBrowserBindingGate
{
    /// <summary>
    /// 确保该调用者/任务在 (contextId, pageId) 上已获授权；**拒绝必须抛异常**（fail-closed，不静默放行）。
    /// 传**完整身份**而不是 (callerId, taskId)：子代理必须带上父任务，否则无法做子集校验（§3.2）。
    /// </summary>
    Task EnsureAsync(
        BrowserCallerIdentity caller,
        string contextId,
        string pageId,
        CancellationToken cancellationToken);
}