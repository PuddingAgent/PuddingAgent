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
/// · `page.snapshot` → `browser.snapshot`（全部快照选项逐字段搬运）；
/// · `page.locate` → `browser.locate`（kind/name/exact/nth/hasText 逐字段；未知 kind 拒绝）；
/// · `page.waitFor` → `browser.wait_for`（单条件映射；多条件/无条件显式拒绝，不静默挑优先级）；
/// · `page.screenshot` → `browser.screenshot`（必须钉住版本；scope/detail/format 严格解析）；
/// · `page.interact` → `browser.interact`（H3：必须带 `expectedPageVersion`；action/scrollTarget 严格解析）；
/// · `page.create` → `browser.tabs`(New，唯一不钉版本的动作)；`page.activate/close` → `browser.tabs`（H3 tabs 变体：必须带 `expectedPageVersion`）。
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
    internal const string LocateCapabilityName = "browser.locate";
    internal const string WaitForCapabilityName = "browser.wait_for";
    internal const string ScreenshotCapabilityName = "browser.screenshot";
    internal const string InteractCapabilityName = "browser.interact";
    internal const string TabsCapabilityName = "browser.tabs";

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

        // page.create 是唯一不需要既有页面目标的命令（新建标签页），其余页面命令必须显式目标。
        if (!string.Equals(command.Name, BrowserBridgeCommandNames.PageCreate, StringComparison.Ordinal)
            && (string.IsNullOrEmpty(command.PageId) || string.IsNullOrEmpty(command.ContextId)))
        {
            reason = "page commands must carry explicit context and page ids";
            return false;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageCreate, StringComparison.Ordinal))
        {
            // 新建标签页是唯一"不钉版本"的 tabs 动作（没有已存在页面可钉）。
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(TabsCapabilityName, out var tabsDescriptor))
            {
                reason = $"capability '{TabsCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            if (string.IsNullOrWhiteSpace(command.ContextId))
            {
                reason = "page.create requires an explicit context id";
                return false;
            }

            PageCreateArguments? createArgs;
            try
            {
                createArgs = command.Arguments.Deserialize<PageCreateArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.create arguments are not valid JSON";
                return false;
            }

            Uri? url = null;
            if (!string.IsNullOrWhiteSpace(createArgs?.InitialUrl))
            {
                if (!Uri.TryCreate(createArgs!.InitialUrl, UriKind.Absolute, out url)
                    || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                {
                    reason = "page.create initialUrl must be an absolute http/https URL";
                    return false;
                }
            }

            request = DesktopCapabilityRequest.ForTabs(BrowserTabsRequest.New(
                command.ContextId.Trim(),
                url,
                createArgs?.Activate ?? true));
            descriptor = tabsDescriptor;
            return true;
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

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageLocate, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(LocateCapabilityName, out descriptor))
            {
                reason = $"capability '{LocateCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            PageLocateArguments? locateArgs;
            try
            {
                locateArgs = command.Arguments.Deserialize<PageLocateArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.locate arguments are not valid JSON";
                return false;
            }

            if (locateArgs?.Locator is not { } wireLocator)
            {
                reason = "page.locate requires a locator";
                return false;
            }

            if (!TryMapLocator(wireLocator, out var locator, out reason))
            {
                return false;
            }

            request = DesktopCapabilityRequest.ForLocate(new BrowserLocateRequest(
                target,
                locator!,
                DesktopPageVersion.Unknown));
            return true;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageWaitFor, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(WaitForCapabilityName, out descriptor))
            {
                reason = $"capability '{WaitForCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            PageWaitForArguments? waitArgs;
            try
            {
                waitArgs = command.Arguments.Deserialize<PageWaitForArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.waitFor arguments are not valid JSON";
                return false;
            }

            waitArgs ??= new PageWaitForArguments();
            var candidates = new (DesktopWaitConditionKind Kind, string? Value)[]
            {
                (DesktopWaitConditionKind.Selector, waitArgs.Selector),
                (DesktopWaitConditionKind.SelectorHidden, waitArgs.SelectorToHide),
                (DesktopWaitConditionKind.UrlPattern, waitArgs.UrlPattern),
            };
            var provided = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
                .ToArray();

            if (provided.Length != 1)
            {
                // 旧 runtime 可同时承载多个条件；新能力契约只有一个条件。
                // 不静默挑优先级（那会改变语义）：多条件/无条件一律拒绝，待契约扩展再放开。
                reason = provided.Length == 0
                    ? "page.waitFor requires one of selector/selectorToHide/urlPattern"
                    : "page.waitFor with multiple conditions is not representable by the current capability contract";
                return false;
            }

            DesktopWaitCondition condition;
            try
            {
                condition = new DesktopWaitCondition(provided[0].Kind, provided[0].Value!);
            }
            catch (ArgumentException ex)
            {
                reason = $"page.waitFor condition is not usable: {ex.Message}";
                return false;
            }

            request = DesktopCapabilityRequest.ForWaitFor(new BrowserWaitForRequest(
                target,
                condition,
                waitArgs.TimeoutMs,
                DesktopPageVersion.Unknown));
            return true;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageScreenshot, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(ScreenshotCapabilityName, out descriptor))
            {
                reason = $"capability '{ScreenshotCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            PageScreenshotArguments? screenshotArgs;
            try
            {
                screenshotArgs = command.Arguments.Deserialize<PageScreenshotArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.screenshot arguments are not valid JSON";
                return false;
            }

            if (screenshotArgs is null || screenshotArgs.ExpectedPageVersion <= 0)
            {
                // 截图必须钉住版本；缺失/非正版本由宿主明确拒绝，不"猜用当前版本"。
                reason = "page.screenshot requires a positive expectedPageVersion";
                return false;
            }

            var scope = DesktopScreenshotScope.Viewport;
            if (!string.IsNullOrWhiteSpace(screenshotArgs.Scope)
                && !DesktopScreenshotScopeWire.TryParse(screenshotArgs.Scope, out scope))
            {
                reason = $"screenshot scope '{screenshotArgs.Scope}' is not registered (no guessing)";
                return false;
            }

            var detail = DesktopScreenshotDetail.Low;
            if (!string.IsNullOrWhiteSpace(screenshotArgs.Detail)
                && !DesktopScreenshotDetailWire.TryParse(screenshotArgs.Detail, out detail))
            {
                reason = $"screenshot detail '{screenshotArgs.Detail}' is not registered (no guessing)";
                return false;
            }

            var format = string.IsNullOrWhiteSpace(screenshotArgs.Format)
                ? "png"
                : screenshotArgs.Format.Trim().ToLowerInvariant();
            if (format is not ("png" or "jpeg"))
            {
                reason = $"screenshot format '{screenshotArgs.Format}' is not supported (png/jpeg only)";
                return false;
            }

            DesktopCapabilityRequest screenshotRequest;
            try
            {
                screenshotRequest = DesktopCapabilityRequest.ForScreenshot(new BrowserScreenshotRequest(
                    target,
                    DesktopPageVersion.Require(screenshotArgs.ExpectedPageVersion),
                    scope,
                    detail,
                    format: format,
                    regionX: screenshotArgs.RegionX,
                    regionY: screenshotArgs.RegionY,
                    regionWidth: screenshotArgs.RegionWidth,
                    regionHeight: screenshotArgs.RegionHeight));
            }
            catch (ArgumentException ex)
            {
                reason = $"page.screenshot request is not usable: {ex.Message}";
                return false;
            }

            request = screenshotRequest;
            return true;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageInteract, StringComparison.Ordinal))
        {
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(InteractCapabilityName, out var interactDescriptor))
            {
                reason = $"capability '{InteractCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            PageInteractArguments? interactArgs;
            try
            {
                interactArgs = command.Arguments.Deserialize<PageInteractArguments>(WebJson);
            }
            catch (JsonException)
            {
                reason = "page.interact arguments are not valid JSON";
                return false;
            }

            if (interactArgs is not { } args || string.IsNullOrWhiteSpace(args.Action))
            {
                reason = "page.interact requires an action";
                return false;
            }

            // H3：写操作必须钉住版本（缺失/非正 ⇒ 拒绝，不猜当前版本、不回退旧路径）。
            if (args.ExpectedPageVersion is not { } wireVersion || wireVersion <= 0)
            {
                reason = "page.interact requires a positive expectedPageVersion on the bridge wire (H3); "
                    + "refusing to guess the current version";
                return false;
            }

            if (!DesktopInteractionActionWire.TryParse(args.Action, out var action))
            {
                reason = $"interaction action '{args.Action}' is not registered (no guessing)";
                return false;
            }

            DesktopLocator? locator = null;
            if (args.Locator is { } wireLocator && !TryMapLocator(wireLocator, out locator, out reason))
            {
                return false;
            }

            DesktopLocator? toLocator = null;

            DesktopScrollTarget? scrollTarget = null;
            if (!string.IsNullOrWhiteSpace(args.ScrollTarget))
            {
                if (!DesktopScrollTargetWire.TryParse(args.ScrollTarget, out var parsedScrollTarget))
                {
                    reason = $"scroll target '{args.ScrollTarget}' is not registered (no guessing)";
                    return false;
                }

                scrollTarget = parsedScrollTarget;
            }

            DesktopCapabilityRequest interactRequest;
            try
            {
                interactRequest = DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                    target,
                    action,
                    DesktopPageVersion.Require(wireVersion),
                    locator,
                    args.Text,
                    args.Values,
                    args.Checked,
                    args.DeltaX,
                    args.DeltaY,
                    // 线缆没有 inputMode：旧 Bridge 路径执行的是 DOM 输入，这里如实为 Dom。
                    DesktopInputMode.Dom,
                    scrollTarget,
                    toLocator,
                    filePaths: null,
                    args.X,
                    args.Y,
                    args.ObservationId,
                    args.ToX,
                    args.ToY));
            }
            catch (ArgumentException ex)
            {
                reason = $"page.interact request is not usable: {ex.Message}";
                return false;
            }

            request = interactRequest;
            descriptor = interactDescriptor;
            return true;
        }

        if (string.Equals(command.Name, BrowserBridgeCommandNames.PageActivate, StringComparison.Ordinal)
            || string.Equals(command.Name, BrowserBridgeCommandNames.PageClose, StringComparison.Ordinal))
        {
            // H3（tabs 变体）：activate/close 是副作用动作，执行器契约要求钉住页面版本。
            if (!Pudding.Contracts.DesktopCapabilities.TryGetByName(TabsCapabilityName, out var tabDescriptor))
            {
                reason = $"capability '{TabsCapabilityName}' is not registered in the capability catalog";
                return false;
            }

            var isActivate = string.Equals(command.Name, BrowserBridgeCommandNames.PageActivate, StringComparison.Ordinal);
            long? wireVersion = null;
            try
            {
                wireVersion = isActivate
                    ? command.Arguments.Deserialize<PageActivateArguments>(WebJson)?.ExpectedPageVersion
                    : command.Arguments.Deserialize<PageCloseArguments>(WebJson)?.ExpectedPageVersion;
            }
            catch (JsonException)
            {
                reason = $"{command.Name} arguments are not valid JSON";
                return false;
            }

            if (wireVersion is not { } pinnedVersion || pinnedVersion <= 0)
            {
                reason = $"{command.Name} requires a positive expectedPageVersion on the bridge wire (H3, tabs); "
                    + "refusing to guess the current version";
                return false;
            }

            request = DesktopCapabilityRequest.ForTabs(new BrowserTabsRequest(
                target,
                isActivate ? DesktopTabAction.Activate : DesktopTabAction.Close,
                DesktopPageVersion.Require(pinnedVersion)));
            descriptor = tabDescriptor;
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

    /// <summary>locate / interact 共用的 locator 映射：kind 严格解析，非法值 fail closed。</summary>
    private static bool TryMapLocator(
        BrowserLocatorDescriptor wireLocator,
        out DesktopLocator? locator,
        out string? reason)
    {
        locator = null;
        reason = null;

        if (!DesktopLocatorKindWire.TryParse(wireLocator.Kind, out var kind))
        {
            reason = $"locator kind '{wireLocator.Kind}' is not registered (no guessing, no fallback)";
            return false;
        }

        try
        {
            locator = new DesktopLocator(
                kind,
                wireLocator.Value,
                wireLocator.Name,
                wireLocator.Exact,
                wireLocator.Nth,
                wireLocator.HasText);
            return true;
        }
        catch (ArgumentException ex)
        {
            reason = $"locator is not usable: {ex.Message}";
            return false;
        }
    }
}
