using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>
/// interact 映射：动作走运行时显式 API（不拼脚本）；**交互后回带新版本**；
/// 版本不符/元素不存在都拒绝且不执行；运行时没有的 `focus` 明确 unsupported。
/// </summary>
public sealed class InteractMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    private static readonly DesktopLocator Button = new(DesktopLocatorKind.Css, "button");

    [Fact]
    public async Task ClickAdvancesTheVersionAndReportsTheElementItActedOn()
    {
        var runtime = Runtime(pageVersion: 7, advanceTo: 8, handle: Handle("e1", version: 7));

        var result = await Surface(runtime).InteractAsync(
            Call, new BrowserInteractRequest(Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(7), Button),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal("click", runtime.Pages["ctx-1/p-1"].LastAction);
        // 交互后必须回带**新**版本（旧 Ref 由此作废）。
        Assert.Equal(8, result.Value.Page.Version.Value);
        // 元素引用带的是**元素自身**的版本（交互前那一版），不是新版本。
        Assert.Equal(7, result.Value.Element!.PageVersion.Value);
    }

    [Fact]
    public async Task FillAndScrollCarryTheirParameters()
    {
        var runtime = Runtime(pageVersion: 3, advanceTo: 4, handle: Handle("e2", version: 3));
        await Surface(runtime).InteractAsync(
            Call,
            new BrowserInteractRequest(Target, DesktopInteractionAction.Fill, DesktopPageVersion.Require(3), Button, text: "hello"),
            CancellationToken.None);
        Assert.Equal("fill:hello", runtime.Pages["ctx-1/p-1"].LastAction);

        // scroll 不需要定位：作用于页面本身，不得去查元素。
        var scrollRuntime = Runtime(pageVersion: 5, advanceTo: 6, handle: null);
        var scroll = await Surface(scrollRuntime).InteractAsync(
            Call,
            new BrowserInteractRequest(Target, DesktopInteractionAction.Scroll, DesktopPageVersion.Require(5), deltaY: -240),
            CancellationToken.None);

        Assert.False(scroll.IsFailure);
        Assert.Equal("scroll:-240", scrollRuntime.Pages["ctx-1/p-1"].LastAction);
        Assert.False(scrollRuntime.Pages["ctx-1/p-1"].Queried);
        Assert.Null(scroll.Value.Element);
    }

    [Fact]
    public async Task FocusIsUnsupportedInsteadOfBeingFakedWithScripts()
    {
        var runtime = Runtime(pageVersion: 4, advanceTo: 5, handle: Handle("e3", version: 4));

        var result = await Surface(runtime).InteractAsync(
            Call, new BrowserInteractRequest(Target, DesktopInteractionAction.Focus, DesktopPageVersion.Require(4), Button),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, result.Error!.Code);
        Assert.Null(runtime.Pages["ctx-1/p-1"].LastAction);
    }

    [Fact]
    public async Task MissingElementAndVersionMismatchAreRejectedWithoutActing()
    {
        var missing = Runtime(pageVersion: 4, advanceTo: 5, handle: null);
        var notFound = await Surface(missing).InteractAsync(
            Call, new BrowserInteractRequest(Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(4), Button),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, notFound.Error!.Code);
        Assert.Null(missing.Pages["ctx-1/p-1"].LastAction);

        var stale = Runtime(pageVersion: 4, advanceTo: 5, handle: Handle("e4", version: 4));
        var mismatch = await Surface(stale).InteractAsync(
            Call, new BrowserInteractRequest(Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(2), Button),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, mismatch.Error!.Code);
        Assert.False(stale.Pages["ctx-1/p-1"].Queried);
    }

    private static BrowserRuntimeDesktopSurface Surface(InteractFakeRuntime runtime) =>
        new(runtime, new InteractFakeTargets());

    private static InteractFakeHandle Handle(string reference, long version) => new(reference, version);

    private static InteractFakeRuntime Runtime(long pageVersion, long advanceTo, InteractFakeHandle? handle)
    {
        var runtime = new InteractFakeRuntime();
        runtime.Contexts.Add(new InteractFakeContext("ctx-1", "p-1", pageVersion, advanceTo, handle, runtime));
        return runtime;
    }

    private sealed class InteractFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class InteractFakeHandle(string reference, long version) : IElementHandle
    {
        public ElementHandleId Id { get; } = new(reference);

        public PageId PageId { get; } = new("p-1");

        public long PageVersion { get; } = version;

        public int? BackendNodeId => 1;

        public string LocatorFingerprint => reference;

        public BrowserElementInfo Info { get; } = new() { Ref = reference, Tag = "button", Visible = true, Enabled = true };

        public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => Task.FromResult<BoundingBox?>(null);

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InteractFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<InteractFakeContext> Contexts { get; } = [];

        public Dictionary<string, InteractFakePage> Pages { get; } = [];

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>(Contexts.Select(c => c.Info).ToArray());

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(Contexts.FirstOrDefault(c => c.Id.Value == id.Value));

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            AsyncEnumerable.Empty<BrowserEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InteractFakeContext : IBrowserContext
    {
        private readonly InteractFakePage _page;

        public InteractFakeContext(
            string id, string pageId, long pageVersion, long advanceTo, InteractFakeHandle? handle, InteractFakeRuntime owner)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo { Id = Id, UserDataDirectory = "C:\\profile\\a", PageCount = 1 };
            _page = new InteractFakePage(pageId, id, pageVersion, advanceTo, handle);
            owner.Pages[$"{id}/{pageId}"] = _page;
        }

        public BrowserContextId Id { get; }

        public BrowserContextInfo Info { get; }

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PageInfo>>([_page.Info]);

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) =>
            Task.FromResult<IBrowserPage?>(_page.Id.Value == id.Value ? _page : null);

        public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task ClosePageAsync(PageId id, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) => throw new NotSupportedException();

        public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => throw new NotSupportedException();

        public Task ClearCookiesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => throw new NotSupportedException();

        public Task ResetPermissionsAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InteractFakePage : IBrowserPage
    {
        private readonly InteractFakeHandle? _handle;

        public InteractFakePage(string id, string contextId, long pageVersion, long advanceTo, InteractFakeHandle? handle)
        {
            Id = new PageId(id);
            ContextId = new BrowserContextId(contextId);
            PageVersion = pageVersion;
            AdvanceTo = advanceTo;
            _handle = handle;
            Info = new PageInfo { Id = Id, ContextId = ContextId, Title = id, Url = "https://example.test/x", PageVersion = pageVersion };
        }

        public long AdvanceTo { get; }

        public bool Queried { get; private set; }

        public string? LastAction { get; private set; }

        public PageId Id { get; }

        public BrowserContextId ContextId { get; }

        public long PageVersion { get; private set; }

        public PageInfo Info { get; private set; }

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        {
            Queried = true;
            return Task.FromResult<IElementHandle?>(_handle);
        }

        private void Advance(string action)
        {
            LastAction = action;
            PageVersion = AdvanceTo;
            Info = Info with { PageVersion = AdvanceTo };
        }

        public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct) { Advance("click"); return Task.CompletedTask; }

        public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct) { Advance($"fill:{value}"); return Task.CompletedTask; }

        public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct) { Advance($"press:{key}"); return Task.CompletedTask; }

        public Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct) { Advance($"check:{isChecked}"); return Task.CompletedTask; }

        public Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct) { Advance($"select:{string.Join(",", values)}"); return Task.CompletedTask; }

        public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct) { Advance("hover"); return Task.CompletedTask; }

        public Task ScrollAsync(ScrollOptions options, CancellationToken ct) { Advance($"scroll:{options.DeltaY}"); return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task BringToFrontAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<System.Text.Json.JsonDocument> SendCdpAsync(string method, System.Text.Json.JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();

        public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => throw new NotSupportedException();

        public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}