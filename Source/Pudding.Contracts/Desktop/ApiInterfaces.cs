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
