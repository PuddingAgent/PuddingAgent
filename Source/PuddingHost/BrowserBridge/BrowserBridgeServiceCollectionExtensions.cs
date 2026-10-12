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

        // §7.1：截图工件发布端 —— `BrowserScreenshotTool` 通过**可选参数**注入它（既有注册不受影响）；
        // **未注册时工具会显式声明看不见图**（`visionUnavailable`），而不是谎报视觉成功。
        //
        // 只在**容器里已有 `VisionArtifactStorageService`** 时注册：测试容器（只装配 Bridge 扩展、
        // 不带 Platform 的视觉存储）若被强行注册，会在激活工具图时抛
        // `Unable to resolve service for type 'VisionArtifactStorageService'`（2026-10-12 实测 3 条回归）。
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PuddingPlatform.Services.VisionArtifactStorageService)))
        {
            services.TryAddSingleton<IBrowserScreenshotPublisher, BrowserScreenshotArtifactPublisher>();
        }

        services.AddSingleton<IDesktopBrowserConnectionRegistry, DesktopBrowserConnectionRegistry>();
        services.AddSingleton<IDesktopBrowserCommandBroker, DesktopBrowserCommandBroker>();
        services.AddSingleton<IBrowserBridgeClock, SystemBrowserBridgeClock>();
        services.AddSingleton<RemoteBrowserRuntime>();

    // 触发点门（§4.1）：把"发命令前确保有授权"接到能力通道的控制面。
    // 会话**按调用解析**（通道由 Desktop 按 desktop.json 启用，可能后连上）⇒ 无条件注册是安全的：
    // 无会话时门是 **no-op**（只走 Bridge 的现网路径行为不变）；最终判据仍在 Desktop 的唯一 authority。
    services.AddSingleton<IBrowserBindingGate>(sp =>
    {
        if (!Pudding.Contracts.DesktopCapabilities.TryGetByName("browser.binding.apply", out var descriptor))
        {
            throw new InvalidOperationException("browser.binding.apply is not registered in the capability catalog");
        }

        return new CapabilityChannelBindingGate(
            () => sp.GetService<Pudding.CapabilityBroker.CapabilityBroker>()?.Sessions.FirstOrDefault(),
            descriptor,
            Pudding.Contracts.Desktop.BrowserPageGrantScope.Read | Pudding.Contracts.Desktop.BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5),
            "bridge:agent-target");
    });
        services.AddSingleton<IBrowserRuntime>(sp => sp.GetRequiredService<RemoteBrowserRuntime>());

        // ── 切片 D 窄端口：组合根按**传输决策**二选一（不做跨传输回退） ──────
        // 优先级与规则来自 CapabilityBroker 的 DesktopTransportRouting：能力通道就绪就走通道，
        // 否则走既有 Bridge，两条都不可用就**如实失败**（绝不换一条重试——那会把操作再做一遍）。
        services.AddSingleton<DesktopTransportUsageTracker>();
        services.AddSingleton<TransportRoutedBrowserCapabilitySurface>(sp =>
            new TransportRoutedBrowserCapabilitySurface(
                new BridgeBrowserCapabilitySurface(sp.GetRequiredService<IBrowserRuntime>()),
                () => sp.GetService<Pudding.CapabilityBroker.CapabilityBroker>()?.Sessions.FirstOrDefault(),
                () => sp.GetService<IDesktopBrowserConnectionRegistry>()?.IsDesktopConnected ?? false,
                sp.GetRequiredService<DesktopTransportUsageTracker>()));

        services.AddSingleton<IDesktopBrowserCapabilitySurface>(sp =>
            sp.GetRequiredService<TransportRoutedBrowserCapabilitySurface>());

        // 缺口 #1：上下文管理端口由**同一个**路由实例实现 ⇒ 两个接口指向同一对象
        //（否则用量计数会分裂，退役 Bridge 的判据就不准了）。
        services.AddSingleton<IDesktopContextCapabilitySurface>(sp =>
            sp.GetRequiredService<TransportRoutedBrowserCapabilitySurface>());

        // Desktop 实例 ID 有**两个真实来源**，都来自各自的握手，绝不猜：
        //   ① 能力通道已启用 ⇒ 活动会话的 DesktopId；
        //   ② 通道未启用 ⇒ 既有 Bridge 连接的 DesktopInstanceId。
        // 少了 ② 这条回退，走 Bridge 的工具根本拿不到调用上下文（本轮实读发现）。
        // 身份读取器（设计 §3.1）：读"本次工具调用"的作用域。注册为单例是因为它**本身无状态** ——
        // 状态在工具执行作用域（AsyncLocal）里，单例只是读取入口。
        services.TryAddSingleton<IBrowserCallerIdentityAccessor, BrowserCallerIdentityAccessor>();

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
            },
            callerIdentity: sp.GetRequiredService<IBrowserCallerIdentityAccessor>()));

        services.AddPuddingToolsFromAssembly(typeof(BrowserContextTool).Assembly);
        return services;
    }
}
