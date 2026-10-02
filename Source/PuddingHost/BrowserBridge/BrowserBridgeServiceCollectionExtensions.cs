using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.BrowserBridge;

public static class BrowserBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the executable Core-side browser proxy and Agent tools only when the
    /// process was launched by PuddingDesktop. Console/dev hosts expose no browser tools.
    /// </summary>
    public static IServiceCollection AddDesktopBrowserAutomation(
        this IServiceCollection services,
        PuddingHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostOptions);

        if (hostOptions.Mode != PuddingHostMode.DesktopChild
            || !hostOptions.BrowserAutomationEnabled)
        {
            return services;
        }

        // Register Origin accessor (AsyncLocal, singleton for process lifetime)
        services.TryAddSingleton<IBrowserOperationOriginAccessor, BrowserOperationOriginAccessor>();

        services.AddSingleton<IDesktopBrowserConnectionRegistry, DesktopBrowserConnectionRegistry>();
        services.AddSingleton<IDesktopBrowserCommandBroker, DesktopBrowserCommandBroker>();
        services.AddSingleton<IBrowserBridgeClock, SystemBrowserBridgeClock>();
        services.AddSingleton<RemoteBrowserRuntime>();
        services.AddSingleton<IBrowserRuntime>(sp => sp.GetRequiredService<RemoteBrowserRuntime>());

        // ── 切片 D 窄端口：组合根按**传输决策**二选一（不做跨传输回退） ──────
        // 优先级与规则来自 CapabilityBroker 的 DesktopTransportRouting：能力通道就绪就走通道，
        // 否则走既有 Bridge，两条都不可用就**如实失败**（绝不换一条重试——那会把操作再做一遍）。
        services.AddSingleton<DesktopTransportUsageTracker>();
        services.AddSingleton<IDesktopBrowserCapabilitySurface>(sp =>
            new TransportRoutedBrowserCapabilitySurface(
                new BridgeBrowserCapabilitySurface(sp.GetRequiredService<IBrowserRuntime>()),
                () => sp.GetService<Pudding.CapabilityBroker.CapabilityBroker>()?.Sessions.FirstOrDefault(),
                () => sp.GetService<IDesktopBrowserConnectionRegistry>()?.IsDesktopConnected ?? false,
                sp.GetRequiredService<DesktopTransportUsageTracker>()));

        // Desktop 实例 ID 有**两个真实来源**，都来自各自的握手，绝不猜：
        //   ① 能力通道已启用 ⇒ 活动会话的 DesktopId；
        //   ② 通道未启用 ⇒ 既有 Bridge 连接的 DesktopInstanceId。
        // 少了 ② 这条回退，走 Bridge 的工具根本拿不到调用上下文（本轮实读发现）。
        services.AddSingleton<IDesktopCapabilityCallContextFactory>(sp =>
            new ConnectedDesktopCallContextFactory(() =>
            {
                if (sp.GetService<Pudding.CapabilityBroker.CapabilityBroker>()?.Sessions is { Count: > 0 } sessions)
                {
                    return sessions[0].DesktopId;
                }

                return sp.GetService<IDesktopBrowserConnectionRegistry>()?.Current is { } bridge
                    && !string.IsNullOrWhiteSpace(bridge.DesktopInstanceId)
                        ? new Pudding.Contracts.DesktopInstanceId(bridge.DesktopInstanceId)
                        : null;
            }));

        services.AddPuddingToolsFromAssembly(typeof(BrowserContextTool).Assembly);
        return services;
    }
}
