namespace Pudding.Contracts.Desktop;

/// <summary>
/// Desktop 侧的 Shell 设施端口：由 WinUI 应用实现（对话框 / 文件选择器 / 剪贴板）。
///
/// 为什么单独一个端口：这些动作需要真实窗口与系统 API，**无法脱离 UI 环境测试**；
/// 把它们与「平台无关的适配逻辑」（预算收敛、取消语义归一、异常→错误映射）分开后，
/// 适配层可以在无 UI 的环境下测试，WinUI 侧只剩对系统 API 的直接调用。
///
/// 契约要求（与 <see cref="IDesktopUiSurface"/> 一致）：
/// · 错误用 <see cref="CapabilityResult{T}"/> 表达，<b>不要抛异常</b>（适配层会兜底，但不该依赖兜底）；
/// · <b>用户取消是结果而不是失败</b>（Canceled），不得伪装成失败；
/// · 剪贴板内容与选中路径属用户隐私：<b>不得写日志或审计</b>；
/// · 只能在 UI 线程被调用（由 DesktopService 经 DispatcherQueue 调度）。
/// </summary>
public interface IDesktopShellFacilities
{
    /// <summary>显示对话框；用户取消返回 <c>Canceled=true</c> 的成功结果。</summary>
    Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken);

    /// <summary>显示文件选择器；取消返回 <c>Canceled=true</c>；返回路径不代表 Core 可读。</summary>
    Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
        DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 读取剪贴板。实现方应遵守请求预算；适配层会再兜一次底
    /// （剪贴板可能含凭据，绝不能无界回传）。
    /// </summary>
    Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken);
}