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

    // 浏览器执行管线（ADR-097 D2）：与能力通道开关**无关**，在工作区就绪时构造一次。
    // 通道只是可选传输；页面登记、版本喂入与执行器都属于浏览器工作区生命周期。
    private IDesktopUiDispatcher? _browserUiDispatcher;
    private IDesktopUiSurface? _browserUiSurface;
    private BrowserTaskBindingService? _browserTaskBindings;
    private BrowserWorkspaceTargetBridge? _browserTargetBridge;
    private IDesktopBrowserExecutor? _browserExecutor;

    /// <summary>浏览器执行器（工作区就绪后恒非空；未就绪时为 null，截图路径会明确失败而非崩溃）。</summary>
    internal IDesktopCapabilityExecutor? CapabilityExecutor =>
        (IDesktopCapabilityExecutor?)_browserExecutor ?? _capabilityChannel?.Service;

    /// <summary>能力通道的执行闸门（挂在浏览器工作区的唯一控制权写入点上）。</summary>
    private IBrowserAutomationGate? _capabilityInteractionGate;

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

            // 浏览器执行管线属于工作区生命周期，与通道开关无关：页面登记/版本喂入/执行器只构造一次。
            // 通道启动只是复用同一批 UI 缝隙与目标登记（ADR-097 D2）。
            InitializeBrowserCapabilityPipeline();
            if (_browserUiDispatcher is not { } dispatcher || _browserUiSurface is not { } surface)
            {
                // 没有可用表面就不启动（fail closed）：起一个表面缺失的通道只会让所有能力调用失败。
                LogCapability("浏览器工作区尚未就绪，本次不启动能力通道");
                return;
            }

            var lifecycle = _browserAutomation;

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
                lifecycle,
                taskBindings: _browserTaskBindings,
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

            // 把能力通道的执行闸门挂到**同一个**控制权写入点上（审计 2026-10-08 结论 #1）：
            // 不挂的话，用户在 UI 上接管浏览器，gRPC 路径上的 Agent 动作照旧执行。
            // 只在这个路径真的启动后挂；RegisterAutomationGate 会自动与当前控制状态对齐
            // （用户可能在通道启动之前就已经接管）。
            if (_capabilityInteractionGate is null && _mainWindow?.BrowserWorkspace is { } workspace)
            {
                // DesktopInteractionState 属于更低层，不能反向引用本组件的端口 ⇒ 用委托闸门映射。
                var interaction = _capabilityChannel.Service.Interaction;
                _capabilityInteractionGate =
                    new DelegateAutomationGate(interaction.SetUserTakeover, interaction.SetPaused);
                workspace.RegisterAutomationGate(_capabilityInteractionGate);
            }

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

            // 先撤闸门再停通道：避免向已释放的路径继续推送控制状态。
            if (_capabilityInteractionGate is { } gate)
            {
                _mainWindow?.BrowserWorkspace?.UnregisterAutomationGate(gate);
                _capabilityInteractionGate = null;
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
    /// 构造**与传输无关**的浏览器执行管线（幂等；UI 线程）：UI 缝隙、控制面、目标登记与执行器
    /// 都在浏览器工作区就绪时一次构造，能力通道开关不参与决定（ADR-097 D2）。
    ///
    /// 顺序：先登记页面（两个注册表 + 真实版本），再构造表面与执行器 —— 否则执行器第一次
    /// 准入会看到"页面未登记/版本未知"。失败只记日志：浏览器页自身会显示初始化失败，
    /// 不得让管线异常阻断 Launcher 或 Core 进程监督。
    /// </summary>
    internal void InitializeBrowserCapabilityPipeline()
    {
        if (_browserExecutor is not null)
        {
            return;
        }

        var window = _mainWindow;
        if (window?.BrowserRuntime is not { } browserRuntime || window.BrowserWorkspace is not { } workspace)
        {
            return;
        }

        try
        {
            var lifecycle = _browserAutomation;

            // 控制面（§4.1）：签发器与受理/撤销服务共用同一 lifecycle 与**真实**连接世代
            // （未握手 ⇒ 签发被拒）。由 DesktopService 侧工厂构造，Shell 不引用 Automation。
            var controlPlane = BrowserTaskBindingComposition.Create(
                lifecycle,
                new DesktopInstanceId("default"),
                new DesktopProcessInstanceId($"desktop-{Environment.ProcessId}"));
            _browserTaskBindings = controlPlane.Service;

            // 页面目标登记上移：默认 Bridge 路径也必须有页面/版本事实，否则页级准入与未来
            // 的截图执行器都会判 `invalid_target` / `page_version_mismatch`。
            _browserTargetBridge = new BrowserWorkspaceTargetBridge(lifecycle, _browserTaskBindings);
            AttachBrowserTargets(workspace, _browserTargetBridge);

            _browserUiDispatcher = new WinUiDesktopUiDispatcher(UiThread.Queue);
            var facilities = new WinUiShellFacilities(
                () => window.CapabilityWindowHandle, () => window.CapabilityXamlRoot);
            var hostFacilities = new WinUiShellHostFacilities(
                () => window.CapabilityWindowState,
                () => window.CapabilityTrayVisible,
                window.ShowCapabilityNotification);
            _browserUiSurface = new DesktopSurfaceComposition(
                browserRuntime,
                lifecycle.BrowserTargets,
                lifecycle.Readings,
                new DesktopShellSurface(facilities, hostFacilities));

            _browserExecutor = DesktopBrowserExecutorComposition.Create(
                _browserUiDispatcher,
                _browserUiSurface,
                lifecycle,
                taskBindings: _browserTaskBindings);

            // I2-c 接线：已映射且已有结果反投影的命令改由工作区执行器执行；
            // 准入/复检仍由派发器（会话权威）完成，执行器见 SessionPermitMarker 不再重复裁决。
            var bridgeExecutor = _browserExecutor;
            if (bridgeExecutor is not null)
            {
                _bridgeDispatcher.SetExecutorRoute(new BrowserBridgeExecutorRoute(
                    (capability, request, context, token) =>
                        ((IDesktopCapabilityExecutor)bridgeExecutor).ExecuteAsync(
                            capability, request, context, token)).RouteAsync);
            }

            LogCapability("浏览器执行管线已就绪（与能力通道开关无关）");
        }
        catch (Exception ex)
        {
            // 失败的管线保持为 null：截图路径会明确失败（不伪造成功），窗口其余功能不受影响。
            _browserExecutor = null;
            _browserUiDispatcher = null;
            _browserUiSurface = null;
            _browserTargetBridge?.Clear();
            _browserTargetBridge = null;
            _browserTaskBindings = null;
            LogCapability($"浏览器执行管线构造失败（{ex.GetType().Name}）");
        }
    }

    /// <summary>窗口退出：先关执行器（让排队中的 UI 调用以 UiUnavailable 结束），再清目标登记。</summary>
    internal async ValueTask DisposeBrowserCapabilityPipelineAsync()
    {
        var executor = _browserExecutor;
        _browserExecutor = null;
        _browserUiDispatcher = null;
        _browserUiSurface = null;
        _browserTaskBindings = null;
        // 先清执行器路由：避免退出期间新命令进入已关闭的执行器。
        _bridgeDispatcher.ClearExecutorRoute();

        if (executor is not null)
        {
            try
            {
                await executor.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 退出路径不抛：关不掉与已关闭等价。
            }
        }

        try
        {
            _browserTargetBridge?.Clear();
        }
        catch
        {
            // 同上。
        }

        _browserTargetBridge = null;
    }

    /// <summary>
    /// 把浏览器工作区的页面生命周期接到两个目标注册表上，并喂**真实页面版本**。
    ///
    /// 版本不再登记为“未知、等第一次能力结果补齐”：默认 Bridge 页面命令由 controller 直接执行，
    /// 不经过 `DesktopService` 的唯一出口；若这里不喂，页面注册表会永远停在 0，未来任何带版本的
    /// 准入（截图执行器）都会被判 `page_version_mismatch`。
    /// </summary>
    private static void AttachBrowserTargets(BrowserWorkspaceController workspace, BrowserWorkspaceTargetBridge bridge)
    {
        string? ContextId() => workspace.ActiveContextId?.Value;

        // 上下文登记必须“先有上下文、后有页面”（生命周期端口的不变式）：工作区可能在管线构造
        // 之后才创建/切换上下文，因此每个事件点都先补登记，避免 OnPageCreated 在 UI 事件里抛异常。
        var registeredContexts = new HashSet<string>(StringComparer.Ordinal);
        void EnsureContext(string contextId)
        {
            if (registeredContexts.Add(contextId))
            {
                bridge.OnContextCreated(contextId);
            }
        }

        DesktopPageVersion VersionOf(PageId pageId) =>
            workspace.TryGetPageVersion(pageId, out var version) && version > 0
                ? DesktopPageVersion.Require(version)
                : DesktopPageVersion.Unknown;

        void Register(string contextId, PageId pageId)
        {
            EnsureContext(contextId);
            bridge.OnPageCreated(
                contextId,
                pageId.Value,
                VersionOf(pageId),
                isActive: workspace.ActivePageId == pageId);
        }

        if (ContextId() is { } initialContext)
        {
            EnsureContext(initialContext);
            foreach (var tab in workspace.Tabs)
            {
                Register(initialContext, tab.PageId);
            }

            bridge.ObserveAgentTarget(initialContext, workspace.AgentTargetPageId?.Value);
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

            EnsureContext(contextId);

            // 版本只前进：导航/交互结束后 controller 会触发这些属性变化；逐个页面补推当前版本，
            // 迟到的旧版本事件不会把注册表拉回去。
            foreach (var tab in workspace.Tabs)
            {
                var version = VersionOf(tab.PageId);
                if (version.IsKnown)
                {
                    bridge.OnPageVersionAdvanced(contextId, tab.PageId.Value, version);
                }
            }

            if (workspace.ActivePageId is { } active)
            {
                bridge.OnPageActivated(contextId, active.Value, VersionOf(active));
            }

            // This is a state observation, not a user revoke command. In particular,
            // startup/ordinary property changes with no selected page must not
            // suspend implicit control of the agent-owned browser.
            bridge.ObserveAgentTarget(contextId, workspace.AgentTargetPageId?.Value);
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
