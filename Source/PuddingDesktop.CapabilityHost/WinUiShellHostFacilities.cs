using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingDesktop.CapabilityHost;

/// <summary>
/// Shell 自身的「存在与告知」端口实现：窗口形态、托盘可见性、系统通知。
///
/// 为什么用委托而不是自己去抓窗口与托盘：
/// · 窗口与托盘的**所有权在 Shell**（<c>MainWindow</c> / <c>DesktopTrayIcon</c>），本工程只被 Shell 引用；
/// · 弹系统通知需要托盘图标的 HWND 与 uID（托盘气泡是托管的既有能力），那正是 Shell 才知道的东西。
/// 因此这里只做「取状态 + 转发通知」，并把结果归一成契约要求的结果类型。
///
/// 契约要点：<b>通知没弹出来也是成功结果</b>（<c>Shown=false</c>），不是失败——
/// 调用方据此知道「通知没到」而不是「能力不可用」。
/// 只读状态里实现方只报告「只有它知道」的部分：自动化状态与打开页面数会被
/// <c>DesktopService</c> 用自身权威状态覆盖，因此这里给保守值即可，不要在这里猜。
/// </summary>
public sealed class WinUiShellHostFacilities : IDesktopShellHostFacilities
{
    private readonly Func<DesktopWindowState> _windowState;
    private readonly Func<bool> _trayVisible;
    private readonly Func<string, string, bool> _showNotification;

    public WinUiShellHostFacilities(
        Func<DesktopWindowState> windowState,
        Func<bool> trayVisible,
        Func<string, string, bool> showNotification)
    {
        _windowState = windowState ?? throw new ArgumentNullException(nameof(windowState));
        _trayVisible = trayVisible ?? throw new ArgumentNullException(nameof(trayVisible));
        _showNotification = showNotification ?? throw new ArgumentNullException(nameof(showNotification));
    }

    public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(CapabilityResult<DesktopShellStatus>.Success(new DesktopShellStatus(
            _windowState(),
            _trayVisible(),
            DesktopAutomationState.Free,
            openPageCount: 0)));
    }

    public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var shown = _showNotification(request.Title, request.Message);

        return Task.FromResult(CapabilityResult<DesktopNotificationResult>.Success(
            new DesktopNotificationResult(shown, NotificationId: null)));
    }
}
