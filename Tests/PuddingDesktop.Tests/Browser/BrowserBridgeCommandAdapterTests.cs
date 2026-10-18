using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// I2-c D1 映射契约的第一片：`page.getInfo` → `webview.page_state`。
/// 先钉住"映射什么/拒绝什么/原因可行动"，再在后续切片切换派发器入口。
/// </summary>
public sealed class BrowserBridgeCommandAdapterTests
{
    [Fact]
    public void PageGetInfo_MapsToThePageStateCapability()
    {
        var command = Command(BrowserBridgeCommandNames.PageGetInfo, "ctx", "page");

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.NotNull(descriptor);
        Assert.Equal(DesktopCapability.WebViewPageState, descriptor!.Capability);
        Assert.NotNull(request);
        Assert.Equal("ctx/page", request!.PageState!.Key);
    }

    [Fact]
    public void ContextList_MapsToTheBrowserContextsCapabilityWithoutAPageTarget()
    {
        // 发现类命令：启动流程要求"没有交页也能看见上下文"，映射层不得要求 page id。
        var command = Command(BrowserBridgeCommandNames.ContextList, null, null);

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.NotNull(descriptor);
        Assert.Equal(DesktopCapability.BrowserContexts, descriptor!.Capability);
        Assert.NotNull(request);
        Assert.NotNull(request!.Contexts);
        Assert.Null(request.PageState);
    }

    [Fact]
    public void PageSnapshot_MapsEverySnapshotOptionField()
    {
        // F4 的教训：映射层必须逐字段搬运，不能"看起来映射了"。
        var command = Command(
            BrowserBridgeCommandNames.PageSnapshot,
            "ctx",
            "page",
            new PageSnapshotArguments
            {
                IncludeDom = false,
                IncludeAccessibilityTree = true,
                IncludeHidden = true,
                IncludeIframes = false,
                IncludeShadowDom = false,
                IncludeHtml = true,
                MaxNodes = 123,
                MaxTextLength = 4567,
                MaxDepth = 9,
                ScopeRef = "v1-n1",
                SinceRevision = "rev-1",
                IncludeContentBlocks = true,
            });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserSnapshot, descriptor!.Capability);
        var snapshot = request!.Snapshot;
        Assert.NotNull(snapshot);
        Assert.Equal("ctx/page", snapshot!.Target.Key);
        Assert.Equal(DesktopPageVersion.Unknown.Value, snapshot.ExpectedPageVersion.Value);

        var options = snapshot.Options;
        Assert.False(options.IncludeDom);
        Assert.True(options.IncludeAccessibilityTree);
        Assert.True(options.IncludeHidden);
        Assert.False(options.IncludeIframes);
        Assert.False(options.IncludeShadowDom);
        Assert.True(options.IncludeHtml);
        Assert.Equal(123, options.MaxNodes);
        Assert.Equal(4567, options.MaxTextLength);
        Assert.Equal(9, options.MaxDepth);
        Assert.Equal("v1-n1", options.ScopeRef);
        Assert.Equal("rev-1", options.SinceRevision);
        Assert.True(options.IncludeContentBlocks);
    }

    [Fact]
    public void PageLocate_MapsTheLocatorFieldsAndRejectsUnknownKinds()
    {
        var command = Command(
            BrowserBridgeCommandNames.PageLocate,
            "ctx",
            "page",
            new PageLocateArguments
            {
                Locator = new BrowserLocatorDescriptor
                {
                    Kind = "label",
                    Value = "Name",
                    Name = "ignored-on-wire-name",
                    Exact = true,
                    Nth = 2,
                    HasText = "hello",
                },
            });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserLocate, descriptor!.Capability);
        var locate = request!.Locate;
        Assert.NotNull(locate);
        Assert.Equal(DesktopLocatorKind.Label, locate!.Locator.Kind);
        Assert.Equal("Name", locate.Locator.Value);
        Assert.Equal("ignored-on-wire-name", locate.Locator.Name);
        Assert.True(locate.Locator.Exact);
        Assert.Equal(2, locate.Locator.Nth);
        Assert.Equal("hello", locate.Locator.HasText);
        // 线缆没有 includeHidden/inViewportOnly：默认 false，不猜 true。
        Assert.False(locate.Locator.IncludeHidden);
        Assert.False(locate.Locator.InViewportOnly);

        var unknown = Command(
            BrowserBridgeCommandNames.PageLocate,
            "ctx",
            "page",
            new PageLocateArguments
            {
                Locator = new BrowserLocatorDescriptor { Kind = "made-up-kind", Value = "x" },
            });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            unknown, out _, out _, out var unknownReason));
        Assert.Contains("not registered", unknownReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageWaitFor_MapsExactlyOneConditionAndRejectsAmbiguousOrEmptyOnes()
    {
        var selectorWait = Command(
            BrowserBridgeCommandNames.PageWaitFor,
            "ctx",
            "page",
            new PageWaitForArguments { Selector = "#ready", TimeoutMs = 1500 });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            selectorWait, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserWaitFor, descriptor!.Capability);
        var wait = request!.WaitFor;
        Assert.NotNull(wait);
        Assert.Equal(DesktopWaitConditionKind.Selector, wait!.Condition.Kind);
        Assert.Equal("#ready", wait.Condition.Value);
        Assert.Equal(1500, wait.TimeoutMs);

        // 多条件：旧 runtime 可全带，新能力契约只有一个条件 —— 不静默挑优先级，直接拒绝。
        var ambiguous = Command(
            BrowserBridgeCommandNames.PageWaitFor,
            "ctx",
            "page",
            new PageWaitForArguments { Selector = "#ready", UrlPattern = "/done" });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            ambiguous, out _, out _, out var ambiguousReason));
        Assert.Contains("multiple conditions", ambiguousReason, StringComparison.Ordinal);

        var empty = Command(BrowserBridgeCommandNames.PageWaitFor, "ctx", "page", new PageWaitForArguments());
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            empty, out _, out _, out var emptyReason));
        Assert.Contains("requires one of", emptyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageScreenshot_MapsPinnedVersionScopeDetailFormatAndRegion()
    {
        var command = Command(
            BrowserBridgeCommandNames.PageScreenshot,
            "ctx",
            "page",
            new PageScreenshotArguments
            {
                ExpectedPageVersion = 7,
                Scope = "region",
                Detail = "high",
                Format = "jpeg",
                RegionX = 10,
                RegionY = 20,
                RegionWidth = 100,
                RegionHeight = 50,
            });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserScreenshot, descriptor!.Capability);
        var screenshot = request!.Screenshot;
        Assert.NotNull(screenshot);
        Assert.Equal(7, screenshot!.ExpectedPageVersion.Value);
        Assert.Equal(DesktopScreenshotScope.Region, screenshot.Scope);
        Assert.Equal(DesktopScreenshotDetail.High, screenshot.Detail);
        Assert.Equal("jpeg", screenshot.Format);
        Assert.Equal(10, screenshot.RegionX);
        Assert.Equal(20, screenshot.RegionY);
        Assert.Equal(100, screenshot.RegionWidth);
        Assert.Equal(50, screenshot.RegionHeight);

        // 缺失/非正版本必须拒绝（不猜当前版本）。
        var missingVersion = Command(
            BrowserBridgeCommandNames.PageScreenshot,
            "ctx",
            "page",
            new PageScreenshotArguments { ExpectedPageVersion = 0 });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            missingVersion, out _, out _, out var versionReason));
        Assert.Contains("positive expectedPageVersion", versionReason, StringComparison.Ordinal);

        // 未注册 scope 必须拒绝（不静默回退 viewport）。
        var badScope = Command(
            BrowserBridgeCommandNames.PageScreenshot,
            "ctx",
            "page",
            new PageScreenshotArguments { ExpectedPageVersion = 3, Scope = "made-up" });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            badScope, out _, out _, out var scopeReason));
        Assert.Contains("not registered", scopeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageCommandsWithoutExplicitTargets_AreRejectedWithAnActionableReason()
    {
        var missingPage = Command(BrowserBridgeCommandNames.PageGetInfo, "ctx", null);
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            missingPage, out _, out _, out var pageReason));
        Assert.Contains("explicit context and page ids", pageReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageGoto_MapsToWebViewNavigateWithActionUrlAndTimeout()
    {
        var command = Command(
            BrowserBridgeCommandNames.PageGoto,
            "ctx",
            "page",
            new PageGotoArguments { Url = "https://example.com/a", TimeoutMs = 1234 });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.WebViewNavigate, descriptor!.Capability);
        var navigation = request!.Navigate;
        Assert.NotNull(navigation);
        Assert.Equal(DesktopNavigationAction.Goto, navigation!.Action);
        Assert.Equal("https://example.com/a", navigation.Url!.AbsoluteUri);
        Assert.Equal(1234, navigation.TimeoutMs);
        Assert.Equal("ctx/page", navigation.Target.Key);
    }

    [Fact]
    public void PageBack_MapsToTheBackActionWithoutAUrl()
    {
        var command = Command(BrowserBridgeCommandNames.PageGoBack, "ctx", "page");

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.WebViewNavigate, descriptor!.Capability);
        Assert.Equal(DesktopNavigationAction.Back, request!.Navigate!.Action);
        Assert.Null(request.Navigate.Url);
    }

    [Fact]
    public void PageGotoWithAnInvalidUrl_IsRejectedWithAnActionableReason()
    {
        var command = Command(
            BrowserBridgeCommandNames.PageGoto,
            "ctx",
            "page",
            new PageGotoArguments { Url = "not-a-url" });

        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason));

        Assert.Null(descriptor);
        Assert.Null(request);
        Assert.Contains("absolute http/https", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageInteract_MapsPinnedVersionActionLocatorAndScrollTargetButRejectsMissingVersion()
    {
        // H3：写操作必须钉住版本；有版本才可执行，缺失/非正则拒绝（不猜当前版本）。
        var command = Command(
            BrowserBridgeCommandNames.PageInteract,
            "ctx",
            "page",
            new PageInteractArguments
            {
                Action = "scroll",
                ExpectedPageVersion = 5,
                Locator = new BrowserLocatorDescriptor { Kind = "css", Value = "#list" },
                DeltaY = 120,
                ScrollTarget = "element",
            });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserInteract, descriptor!.Capability);
        var interact = request!.Interact;
        Assert.NotNull(interact);
        Assert.Equal(DesktopInteractionAction.Scroll, interact!.Action);
        Assert.Equal(5, interact.ExpectedPageVersion.Value);
        Assert.Equal("#list", interact.Locator!.Value);
        Assert.Equal(120, interact.DeltaY);
        Assert.Equal(DesktopScrollTarget.Element, interact.ScrollTarget);
        Assert.Equal(DesktopInputMode.Dom, interact.InputMode);

        var missingVersion = Command(
            BrowserBridgeCommandNames.PageInteract,
            "ctx",
            "page",
            new PageInteractArguments
            {
                Action = "click",
                Locator = new BrowserLocatorDescriptor { Kind = "css", Value = "#go" },
            });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            missingVersion, out var noVersionDescriptor, out var noVersionRequest, out var versionReason));
        Assert.Null(noVersionDescriptor);
        Assert.Null(noVersionRequest);
        Assert.Contains("expectedPageVersion", versionReason, StringComparison.Ordinal);
        Assert.Contains("H3", versionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageCreate_MapsToTabsNewWithoutAPinnedVersionButActivateIsBlockedByTheTabsH3Gap()
    {
        var command = Command(
            BrowserBridgeCommandNames.PageCreate,
            "ctx",
            null,
            new PageCreateArguments { InitialUrl = "https://example.com/new", Activate = false });

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.Equal(DesktopCapability.BrowserTabs, descriptor!.Capability);
        var tabs = request!.Tabs;
        Assert.NotNull(tabs);
        Assert.Equal(DesktopTabAction.New, tabs!.Action);
        Assert.Null(tabs.Target);
        Assert.Equal("ctx", tabs.ContextId);
        Assert.Equal("https://example.com/new", tabs.Url!.AbsoluteUri);
        Assert.False(tabs.Activate);
        Assert.False(tabs.ExpectedPageVersion.IsKnown);

        // activate/close 与 interact 同类：契约要求钉版本，线缆已补字段 ⇒ 有版本可映射、缺失拒绝。
        var activate = Command(
            BrowserBridgeCommandNames.PageActivate,
            "ctx",
            "page",
            new PageActivateArguments { PageId = "page", ExpectedPageVersion = 9 });
        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            activate, out var activateDescriptor, out var activateRequest, out var activateReason), activateReason);
        Assert.Equal(DesktopCapability.BrowserTabs, activateDescriptor!.Capability);
        Assert.Equal(DesktopTabAction.Activate, activateRequest!.Tabs!.Action);
        Assert.Equal(9, activateRequest.Tabs.ExpectedPageVersion.Value);

        var closeWithoutVersion = Command(
            BrowserBridgeCommandNames.PageClose,
            "ctx",
            "page",
            new PageCloseArguments { PageId = "page" });
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            closeWithoutVersion, out _, out _, out var closeReason));
        Assert.Contains("expectedPageVersion", closeReason, StringComparison.Ordinal);
        Assert.Contains("H3", closeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmappedCommands_FailClosedInsteadOfFallingBack()
    {
        // 尚未迁移的命令不得"看起来成功"或回退到 controller：返回 false 并给出原因。
        var command = Command(BrowserBridgeCommandNames.PageList, "ctx", "page");
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason));

        Assert.Null(descriptor);
        Assert.Null(request);
        Assert.Contains("not mapped", reason, StringComparison.Ordinal);
    }

    private static BrowserBridgeCommand Command(
        string name,
        string? contextId,
        string? pageId,
        object? args = null) => new()
    {
        OperationId = Guid.NewGuid(),
        Name = name,
        ContextId = contextId,
        PageId = pageId,
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Arguments = JsonSerializer.SerializeToElement(
            args ?? new { },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };
}
