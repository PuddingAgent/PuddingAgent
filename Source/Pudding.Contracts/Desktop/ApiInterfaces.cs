namespace Pudding.Contracts.Desktop;

/// <summary>
/// Desktop 原生能力的聚合入口（平台无关；WinUI/WebView2 实现在消费方程序集）。
/// </summary>
public interface IPuddingDesktopApi
{
    IPuddingDesktopWebViewApi WebView { get; }

    IPuddingDesktopShellApi Shell { get; }
}

/// <summary>
/// 浏览器/WebView 能力。所有调用都带显式目标与取消；实现方负责把动作调度到 UI 线程。
/// </summary>
public interface IPuddingDesktopWebViewApi
{
    Task<CapabilityResult<NavigateResult>> NavigateAsync(
        DesktopCallContext context,
        NavigateRequest request,
        CancellationToken cancellationToken = default);

    Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        DesktopCallContext context,
        JavascriptRequest request,
        CancellationToken cancellationToken = default);

    Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context,
        DesktopPageTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>读取剪贴板文本（v1 只读；预算由请求给出，截断必须如实标注）。</summary>
    Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context,
        ClipboardReadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>标签页操作：激活或关闭目标页面（变更类；必须固定页面版本）。</summary>
    Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        DesktopCallContext context,
        BrowserTabsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>列出浏览器上下文与页面（只读；浏览器作用域，无页面目标）。</summary>
    Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext context,
        CancellationToken cancellationToken = default);

    /// <summary>等待条件满足（只读）；超时用 TimedOut 标注而不是失败。</summary>
    Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        DesktopCallContext context,
        BrowserWaitForRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>对元素执行交互（变更类；必须固定页面版本，交互后旧 Ref 作废）。</summary>
    Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        DesktopCallContext context,
        BrowserInteractRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>按定位描述符查找元素（返回带 PageVersion 的 Ref；命中 0 个不是错误）。</summary>
    Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        DesktopCallContext context,
        BrowserLocateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>读取页面快照（DOM/可访问性树 + PageVersion）。Ref 只在同一 PageVersion 内有效。</summary>
    Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        DesktopCallContext context,
        BrowserSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Shell 能力。本轮有类型化 DTO 的是通知与只读状态；对话框/Picker/剪贴板在后续切片逐能力开放。</summary>
public interface IPuddingDesktopShellApi
{
    Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context,
        DesktopNotificationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>只读 Shell 状态（窗口形态/托盘/自动化状态/打开页面数）。</summary>
    Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context,
        CancellationToken cancellationToken = default);
}
