using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// Shell 可见的**浏览器执行器**端口：一个与传输无关的 <see cref="IDesktopCapabilityExecutor"/>，
/// 在浏览器工作区生命周期内构造（不依赖能力通道是否启用），并提供显式关闭入口。
///
/// 为什么需要这个端口而不是让 Shell 直接 new <see cref="DesktopService"/>：
/// <c>DesktopService</c> 的构造签名里出现 <c>PuddingBrowser.Automation</c> 的类型
/// （<c>BrowserAutomationAuthority</c>），而 Shell 的引用白名单不包含 Automation；
/// 让构造只发生在本组件内，Shell 只拿到的类型就能正常编译（先例：<c>BrowserTaskBindingComposition</c>）。
/// </summary>
public interface IDesktopBrowserExecutor : IDesktopCapabilityExecutor, IAsyncDisposable
{
    /// <summary>本执行器读取/写入的**同一份**控制面（暂停/接管），Shell 用于诊断与闸门。</summary>
    DesktopInteractionState Interaction { get; }

    bool IsClosed { get; }

    /// <summary>拒绝新调用并让排队中的 UI 调用以 UiUnavailable 结束（窗口退出路径）。</summary>
    void Close();
}

/// <summary>
/// 浏览器执行器的组合根：浏览器工作区就绪即构造，与能力通道（gRPC）生命周期无关。
///
/// 三条语义：
/// ① 构造器只接收生命周期端口（唯一权威 + 两个注册表）与 Shell 提供的 UI 缝隙，**不接收**能力通道对象；
/// ② `closesAuthorityOnDispose` 区分「拥有共享权威关闭权」（浏览器工作区执行器）与「借用」
///    （能力通道执行器）——否则 Core 每次重启停通道都会把 Bridge 仍要用的共享权威永久关闭；
/// ③ 本类不碰 WinUI/WebView2，所有 UI 动作经 <see cref="IDesktopUiDispatcher"/>/<see cref="IDesktopUiSurface"/>。
/// </summary>
public static class DesktopBrowserExecutorComposition
{
    /// <summary>
    /// 构造 Shell 可见的浏览器执行器。默认拥有共享权威关闭权（窗口退出时关闭自动化）。
    /// </summary>
    public static IDesktopBrowserExecutor Create(
        IDesktopUiDispatcher dispatcher,
        IDesktopUiSurface surface,
        BrowserPageLifecycle lifecycle,
        DesktopServiceOptions? options = null,
        TimeProvider? timeProvider = null,
        BrowserTaskBindingService? taskBindings = null,
        bool closesAuthorityOnDispose = true)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new DesktopBrowserExecutor(CreateService(
            dispatcher,
            surface,
            lifecycle,
            options,
            timeProvider,
            taskBindings,
            closesAuthorityOnDispose));
    }

    /// <summary>
    /// 直接构造执行器服务（组件内使用；Shell 不应调用 —— 它只需要 <see cref="Create"/>）。
    /// </summary>
    public static DesktopService CreateService(
        IDesktopUiDispatcher dispatcher,
        IDesktopUiSurface surface,
        BrowserPageLifecycle lifecycle,
        DesktopServiceOptions? options = null,
        TimeProvider? timeProvider = null,
        BrowserTaskBindingService? taskBindings = null,
        bool closesAuthorityOnDispose = true)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new DesktopService(
            dispatcher,
            surface,
            lifecycle.PageTargets,
            options ?? DesktopCapabilityChannelComposition.CreateDefaultServiceOptions(),
            lifecycle.Authority,
            timeProvider,
            // 生产路径必须提供连接世代：否则准入闸门不生效（等于没有准入）。
            () => lifecycle.Authority.ConnectionGeneration,
            taskBindings,
            closesAuthorityOnDispose);
    }

    private sealed class DesktopBrowserExecutor : IDesktopBrowserExecutor
    {
        private readonly DesktopService _service;

        public DesktopBrowserExecutor(DesktopService service)
            => _service = service ?? throw new ArgumentNullException(nameof(service));

        public DesktopInteractionState Interaction => _service.Interaction;

        public bool IsClosed => _service.IsClosed;

        public Task<DesktopCapabilityResponse> ExecuteAsync(
            DesktopCapabilityDescriptor capability,
            DesktopCapabilityRequest request,
            DesktopCallContext context,
            CancellationToken cancellationToken = default)
            => _service.ExecuteAsync(capability, request, context, cancellationToken);

        public void Close() => _service.Close();

        public ValueTask DisposeAsync() => _service.DisposeAsync();
    }
}
