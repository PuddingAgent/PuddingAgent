using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 迁移期的传输用量记数（线程安全）。切片 D 的退役判据（"窗口内零回退"）就读它。
/// </summary>
internal sealed class DesktopTransportUsageTracker
{
    private readonly object _gate = new();
    private DesktopTransportUsage _usage = new(0, 0, 0);

    public DesktopTransportUsage Current
    {
        get
        {
            lock (_gate)
            {
                return _usage;
            }
        }
    }

    public void Record(DesktopTransportRoute route)
    {
        lock (_gate)
        {
            _usage = _usage.Record(route);
        }
    }
}

/// <summary>
/// 组合根按**传输决策**二选一的窄端口实现——切片 D 第③步的最后一环。
///
/// 规则全部来自 <see cref="DesktopTransportRouting"/>，这里不另立一套：唯一的硬要求是
/// **同一次操作绝不执行两次**（通道超时不代表 Desktop 没执行，回退会把它再做一遍），
/// 因此每次调用只做一次决策、只调用一侧，并如实记数（含无路由的失败）。
/// </summary>
internal sealed class TransportRoutedBrowserCapabilitySurface(
    IDesktopBrowserCapabilitySurface legacyBridge,
    Func<IDesktopBrowserCapabilitySurface?> activeChannel,
    Func<bool> legacyBridgeAvailable,
    DesktopTransportUsageTracker usage) : IDesktopBrowserCapabilitySurface
{
    public Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.GetContextsAsync(call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopPageTarget target, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.GetPageStateAsync(target, call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.TabsAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<NavigateResult>> NavigateAsync(
        NavigateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.NavigateAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.SnapshotAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.LocateAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.InteractAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.WaitForAsync(request, call, ct), cancellationToken);

    public Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        JavascriptRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync((surface, ct) => surface.ExecuteJavascriptAsync(request, call, ct), cancellationToken);

    private async Task<CapabilityResult<T>> InvokeAsync<T>(
        Func<IDesktopBrowserCapabilitySurface, CancellationToken, Task<CapabilityResult<T>>> invoke,
        CancellationToken cancellationToken)
    {
        var channel = activeChannel();
        var decision = DesktopTransportRouting.Decide(
            channelReady: channel is not null,
            // 每次调用只尝试一次通道：进入这里时本次操作尚未碰过通道。
            channelAttempted: false,
            legacyBridgeAvailable: legacyBridgeAvailable());

        switch (decision.Route)
        {
            case DesktopTransportRoute.CapabilityChannel when channel is not null:
                usage.Record(DesktopTransportRoute.CapabilityChannel);
                return await invoke(channel, cancellationToken).ConfigureAwait(false);

            case DesktopTransportRoute.LegacyBridge:
                usage.Record(DesktopTransportRoute.LegacyBridge);
                return await invoke(legacyBridge, cancellationToken).ConfigureAwait(false);

            default:
                // 两条都不可用：**如实失败**，绝不自行换一条重试（换一条会把操作再做一遍）。
                usage.Record(DesktopTransportRoute.None);
                return CapabilityResult<T>.Failure(DesktopTransportRouting.NoRoute(decision));
        }
    }
}
