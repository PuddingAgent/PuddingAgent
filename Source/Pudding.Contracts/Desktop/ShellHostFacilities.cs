namespace Pudding.Contracts.Desktop;

/// <summary>
/// Shell 自身的「存在与告知」端口：窗口形态、托盘可见性、系统通知。由 <b>Shell 应用</b>实现。
///
/// 为什么与 <see cref="IDesktopShellFacilities"/> 分开（不是重复）：
/// · 那三项（对话框/文件选择器/剪贴板）是<b>用户交互</b>，实现方在 WinUI 中间层，
///   只需要窗口句柄与 XamlRoot，可以脱离 Shell 的窗口/托盘所有权存在；
/// · 这两项是<b>宿主自我描述与告知</b>：只有 Shell 知道「窗口是否已隐藏到托盘」「托盘是否还在」，
///   而弹系统通知需要托盘/窗口句柄与托盘图标的所有权（现有实现是 Shell 内的 <c>DesktopTrayIcon</c>）。
///
/// 合成一个接口会让实现类同时承担两种所有权与两种线程假设，且让适配层无法在无 Shell 的环境下测试。
///
/// 契约要求（与其他接缝一致）：
/// · 错误用 <see cref="CapabilityResult{T}"/> 表达，不要抛异常；
/// · 通知<b>没弹出来也是结果</b>（<c>Shown=false</c>），不是失败；
/// · 不返回任何页面内容、URL、剪贴板或凭据。
/// </summary>
public interface IDesktopShellHostFacilities
{
    /// <summary>
    /// 只读 Shell 状态：实现方只报告「只有它知道」的部分——窗口形态与托盘可见性。
    /// <b>自动化状态与打开页面数会被 DesktopService 用自身权威状态覆盖</b>，
    /// 实现方给保守值（<see cref="DesktopAutomationState.Free"/>、<c>0</c>）即可，不要在这里猜。
    /// </summary>
    Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 显示系统通知。未成功弹出（托盘不可用/系统策略）必须返回 <c>Shown=false</c> 的成功结果，
    /// 不得折叠成失败——调用方据此知道「通知没到」而不是「能力不可用」。
    /// </summary>
    Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken);
}
