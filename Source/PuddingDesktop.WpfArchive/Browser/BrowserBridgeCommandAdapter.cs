using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// I2-c D1：把 Bridge 命令映射成**能力请求**（尚不接管执行；执行仍由 controller）。
///
/// 已映射：
/// · `context.list` → `browser.contexts`（发现类，不要求显式页面目标）；
/// · `page.getInfo` → `webview.page_state`；
/// · `page.goto/goBack/goForward/reload/stop` → `webview.navigate`（动作 + URL + 超时）；
/// · `page.snapshot` → `browser.snapshot`（全部快照选项逐字段搬运）。
///
/// 后续切片按"快照/定位/等待 → 交互/tabs → 截图"扩充，再切换派发器入口。映射失败的
/// <c>reason</c> 必须可行动（缺 context/page id、参数非法、能力未注册、尚未映射），不得
/// 静默回退到 controller；切换入口后该 reason 直接成为拒绝码来源。
/// </summary>
public static class BrowserBridgeCommandAdapter
{
    internal const string PageStateCapabilityName = "webview.page_state";
    internal const string ContextsCapabilityName = "browser.contexts";
    internal const string NavigateCapabilityName = "webview.navigate";
    internal const string SnapshotCapabilityName = "browser.snapshot";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static bool TryMapReadCommand(
        BrowserBridgeCommand command,
        out DesktopCapabilityDescriptor? descriptor,
        out DesktopCapabilityRequest? request,
        out string? reason)
    {
        descriptor = null;
        request = null;
        reason = null;

        if (string.Equals(command.Name, BrowserBridgeCommandNames.ContextList, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(ContextsCapabilityName, out descriptor))
            {
                reason = $"capability '{ContextsCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            request = DesktopCapabilityRequest.ForContexts();
            return true;
        }

        if (string.IsNullOrEmpty(command.PageId) || string.IsNullOrEmpty(command.ContextId))
        {
            reason = "page commands must carry explicit context and page ids";
            return false;
        }

        var target = new DesktopPageTarget(command.ContextId, command.PageId);

        if (TryMapNavigation(command, target, out request, out reason))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(NavigateCapabilityName, out descriptor))
            {
                request = null;
                reason = $"capability '{NavigateCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            return true;
        }

        // 导航类命令但参数非法：保留可行动原因，不再往下当作"未映射"。
        if (reason is not null)
        {
            return false;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageSnapshot, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(SnapshotCapabilityName, out descriptor))
            {
                reason = $"capability '{SnapshotCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            PageSnapshotArguments? snapshotArgs;
            try
            {
                snapshotArgs = command.Arguments.Deserialize<PageSnapshotArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.snapshot arguments are not valid JSON";
                return false;
            }

            var options = snapshotArgs is null
                ? new DesktopSnapshotOptions()
                : new DesktopSnapshotOptions(
                    includeDom: snapshotArgs.IncludeDom,
                    includeAccessibilityTree: snapshotArgs.IncludeAccessibilityTree,
                    includeHtml: snapshotArgs.IncludeHtml,
                    maxNodes: snapshotArgs.MaxNodes,
                    maxTextLength: snapshotArgs.MaxTextLength,
                    includeHidden: snapshotArgs.IncludeHidden,
                    includeIframes: snapshotArgs.IncludeIframes,
                    includeShadowDom: snapshotArgs.IncludeShadowDom,
                    maxDepth: snapshotArgs.MaxDepth,
                    scopeRef: snapshotArgs.ScopeRef,
                    sinceRevision: snapshotArgs.SinceRevision,
                    includeContentBlocks: snapshotArgs.IncludeContentBlocks);

            request = DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(
                target,
                DesktopPageVersion.Unknown,
                options));
            return true;
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

        request = DesktopCapabilityRequest.ForPageState(target);
        return true;
    }

    private static bool TryMapNavigation(
        BrowserBridgeCommand command,
        DesktopPageTarget target,
        out DesktopCapabilityRequest? request,
        out string? reason)
    {
        request = null;
        reason = null;

        DesktopNavigationAction action;
        switch (command.Name)
        {
            case BrowserBridgeCommandNames.PageGoto:
                action = DesktopNavigationAction.Goto;
                break;
            case BrowserBridgeCommandNames.PageGoBack:
                action = DesktopNavigationAction.Back;
                break;
            case BrowserBridgeCommandNames.PageGoForward:
                action = DesktopNavigationAction.Forward;
                break;
            case BrowserBridgeCommandNames.PageReload:
                action = DesktopNavigationAction.Reload;
                break;
            case BrowserBridgeCommandNames.PageStop:
                action = DesktopNavigationAction.Stop;
                break;
            default:
                return false;
        }

        if (action == DesktopNavigationAction.Goto)
        {
            PageGotoArguments? gotoArgs;
            try
            {
                gotoArgs = command.Arguments.Deserialize<PageGotoArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.goto arguments are not valid JSON";
                return false;
            }

            if (gotoArgs is null || string.IsNullOrWhiteSpace(gotoArgs.Url))
            {
                reason = "page.goto requires a non-empty url";
                return false;
            }

            if (!Uri.TryCreate(gotoArgs.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                reason = "page.goto url must be an absolute http/https URL";
                return false;
            }

            request = DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
                target,
                uri,
                DesktopPageVersion.Unknown,
                action,
                gotoArgs.TimeoutMs));
            return true;
        }

        request = DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
            target,
            url: null,
            DesktopPageVersion.Unknown,
            action));
        return true;
    }
}
