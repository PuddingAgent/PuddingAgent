using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;
using PuddingDesktop.Browser;
using PuddingDesktop.CapabilityHost;
using PuddingDesktop.Configuration;
using PuddingDesktop.Core;
using PuddingBrowser.Abstractions;

namespace PuddingDesktop.Hosting;

/// <summary>
/// 能力通道接线（切片 C-3）。单独一个 partial 文件：组合根主文件已很长，而这一段是自包含的。
///
/// 分工（**Shell 里不留判断逻辑**）：
/// · 判定与装配在 `Pudding.DesktopService.DesktopCapabilityChannelComposition`（可脱 UI 测试）；
/// · 页面生命周期 → 目标注册表的翻译在 `BrowserWorkspaceTargetBridge`（可脱 UI 测试）；
/// · 这里只做「取只有 Shell 知道的事实 → 调一次 → 退出时释放」。
///
/// 默认关闭：`desktop.json` 的 `desktop:capabilityChannel` 缺席即为关闭，走既有 WebSocket Bridge，
/// 行为与加这段代码之前一致（回滚 = 保持/恢复该段缺席或 `enabled=false`）。
/// </summary>
public sealed partial class DesktopApplicationCoordinator
{
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private readonly SemaphoreSlim _capabilityStartGate = new(1, 1);
    private DesktopCapabilityChannelComposition? _capabilityChannel;

    private void LogCapability(string message) => CoreLogBuffer.Append($"[CapabilityChannel] {message}");

    /// <summary>Core 就绪后调用。关闭态什么都不做；启用态失败必须**可见**（不是只留一条日志）。</summary>
    internal async Task StartCapabilityChannelAsync(CoreProcessSession session, CancellationToken cancellationToken)
    {
        // 启动本身要串行：Core 状态变化可能连续触发。
        await _capabilityStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.CapabilityEndpoint is null
                && _bootstrapSettings?.Desktop?.CapabilityChannel is not { Enabled: true })
            {
                // 关闭态（绝大多数情况）：不构造、不连接、不改行为。
                return;
            }

            if (_capabilityChannel is not null)
            {
                return;
            }

            var settings = MapCapabilitySettings(_bootstrapSettings?.Desktop?.CapabilityChannel);
            if (!settings.Enabled)
            {
                LogCapability("未启用（desktop.json 的 desktop:capabilityChannel 缺省关闭），继续走既有 WebSocket Bridge");
                return;
            }

            var window = _mainWindow;
            if (window?.BrowserRuntime is not { } browserRuntime || window.BrowserWorkspace is not { } workspace)
            {
                // 没有可用表面就不启动（fail closed）：起一个表面缺失的通道只会让所有能力调用失败。
                LogCapability("浏览器工作区尚未就绪，本次不启动能力通道");
                return;
            }

            var pageTargets = new DesktopTargetRegistry();
            var browserTargets = new BrowserTargetRegistry();
            var bridge = new BrowserWorkspaceTargetBridge(browserTargets, pageTargets);
            AttachBrowserTargets(workspace, bridge);

            var dispatcher = new WinUiDesktopUiDispatcher(UiThread.Queue);
            var facilities = new WinUiShellFacilities(
                () => window.CapabilityWindowHandle, () => window.CapabilityXamlRoot);
            var hostFacilities = new WinUiShellHostFacilities(
                () => window.CapabilityWindowState,
                () => window.CapabilityTrayVisible,
                window.ShowCapabilityNotification);
            var surface = new DesktopSurfaceComposition(
                new BrowserRuntimeDesktopSurface(browserRuntime, browserTargets),
                new DesktopShellSurface(facilities, hostFacilities));

            string token;
            try
            {
                token = await GetDesktopControlTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCapability($"控制令牌不可用，不启动能力通道（{ex.GetType().Name}）");
                return;
            }

            // 缺省服务策略 = 实现即声明的能力集合 + Shell 调用方 Untrusted（fail closed，
            // 等 Core 侧授权链路给出可信调用方身份再放宽）。
            var result = await DesktopCapabilityChannelComposition.StartAsync(
                settings,
                session.CapabilityEndpoint,
                DesktopChannelAuthentication.StaticHeader(settings.ControlTokenHeader, token),
                new DesktopProcessInstanceId($"desktop-{Environment.ProcessId}"),
                dispatcher,
                surface,
                pageTargets,
                log: LogCapability,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                LogCapability($"未启动：{result.Error!.Code} — {result.Error.Message}");
                return;
            }

            if (result.Value is null)
            {
                LogCapability("预检判定不启动（不猜端点、不回退旧传输）");
                return;
            }

            _capabilityChannel = result.Value;
            _capabilityChannel.Host.StateChanged += state => LogCapability($"连接状态：{state}");
            LogCapability("已启动：启用即走能力通道，不回退旧 Bridge");
        }
        catch (OperationCanceledException)
        {
            // Desktop 关闭拥有取消。
        }
        catch (Exception ex)
        {
            LogCapability($"接线失败（{ex.GetType().Name}）");
        }
        finally
        {
            _capabilityStartGate.Release();
        }
    }

    /// <summary>Core 停止/退出/重启时释放：归还「同一 DesktopId 单一活动传输」的名额。</summary>
    internal async Task StopCapabilityChannelAsync()
    {
        await _capabilityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_capabilityChannel is not { } channel)
            {
                return;
            }

            _capabilityChannel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
            LogCapability("已停止并释放传输名额");
        }
        catch (Exception ex)
        {
            LogCapability($"停止失败（{ex.GetType().Name}）");
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    /// <summary>
    /// 把浏览器工作区的页面生命周期接到两个目标注册表上。页面**版本**不在这里喂：
    /// `DesktopService` 在唯一出口把能力结果里的版本回写（那才是准入的比较基准），
    /// 这里登记为"尚未观测"，由第一次能力结果补齐。
    /// </summary>
    private static void AttachBrowserTargets(BrowserWorkspaceController workspace, BrowserWorkspaceTargetBridge bridge)
    {
        string? ContextId() => workspace.ActiveContextId?.Value;

        void Register(string contextId, PageId pageId) =>
            bridge.OnPageCreated(
                contextId,
                pageId.Value,
                DesktopPageVersion.Unknown,
                isActive: workspace.ActivePageId == pageId);

        if (ContextId() is { } initialContext)
        {
            bridge.OnContextCreated(initialContext);
            foreach (var tab in workspace.Tabs)
            {
                Register(initialContext, tab.PageId);
            }

            bridge.OnAgentTargetChanged(initialContext, workspace.AgentTargetPageId?.Value);
        }

        workspace.Tabs.CollectionChanged += (_, e) =>
        {
            if (ContextId() is not { } contextId)
            {
                return;
            }

            if (e.NewItems is not null)
            {
                foreach (var item in e.NewItems.OfType<BrowserTabViewModel>())
                {
                    Register(contextId, item.PageId);
                }
            }

            if (e.OldItems is not null)
            {
                foreach (var item in e.OldItems.OfType<BrowserTabViewModel>())
                {
                    bridge.OnPageClosed(contextId, item.PageId.Value);
                }
            }
        };

        workspace.PropertyChanged += (_, _) =>
        {
            if (ContextId() is not { } contextId)
            {
                return;
            }

            if (workspace.ActivePageId is { } active)
            {
                bridge.OnPageActivated(contextId, active.Value, DesktopPageVersion.Unknown);
            }

            bridge.OnAgentTargetChanged(contextId, workspace.AgentTargetPageId?.Value);
        };
    }

    /// <summary>文件形态 → 组件设置类型的**纯字段拷贝**（映射不可下沉到组件，见规格 §8.3）。</summary>
    private static DesktopCapabilityChannelSettings MapCapabilitySettings(
        DesktopCapabilityChannelFileSettings? section) =>
        section is null
            ? DesktopCapabilityChannelSettings.Disabled
            : new DesktopCapabilityChannelSettings
            {
                Enabled = section.Enabled,
                DesktopId = section.DesktopId,
                ControlTokenHeader = section.ControlTokenHeader,
                HandshakeTimeoutSeconds = section.HandshakeTimeoutSeconds,
            };
}
