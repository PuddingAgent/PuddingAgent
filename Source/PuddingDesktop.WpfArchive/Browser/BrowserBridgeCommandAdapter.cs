using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// I2-c D1 的第一步：把 Bridge 命令映射成**能力请求**（尚不接管执行；执行仍由 controller）。
///
/// 本轮只映射可验证的读命令 `page.getInfo`，用于钉住映射契约与失败原因；后续切片按
/// "读命令（snapshot/locate/waitFor）→ 写命令（navigate/interact/tabs）→ 截图"扩充，
/// 再切换派发器入口。映射失败的 <c>reason</c> 必须可行动（缺 context/page id、能力未注册、
/// 尚未映射），不得静默回退到 controller；切换入口后该 reason 直接成为拒绝码来源。
/// </summary>
public static class BrowserBridgeCommandAdapter
{
    internal const string PageStateCapabilityName = "webview.page_state";

    public static bool TryMapReadCommand(
        BrowserBridgeCommand command,
        out DesktopCapabilityDescriptor? descriptor,
        out DesktopCapabilityRequest? request,
        out string? reason)
    {
        descriptor = null;
        request = null;
        reason = null;

        if (string.IsNullOrEmpty(command.PageId) || string.IsNullOrEmpty(command.ContextId))
        {
            reason = "page commands must carry explicit context and page ids";
            return false;
        }

        if (!string.Equals(command.Name, BrowserBridgeCommandNames.PageGetInfo, StringComparison.Ordinal))
        {
            reason = $"command '{command.Name}' is not mapped to the desktop executor yet";
            return false;
        }

        if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(PageStateCapabilityName, out descriptor))
        {
            reason = $"capability '{PageStateCapabilityName}' is not registered in the capability catalog";
            return false;
        }

        request = DesktopCapabilityRequest.ForPageState(
            new DesktopPageTarget(command.ContextId, command.PageId));
        return true;
    }
}
