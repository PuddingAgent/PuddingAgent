using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>wait_for 映射：**超时是正常结果而不是失败**；条件按类型映射到运行时；版本固定仍受检查。</summary>
public sealed class WaitForMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task SatisfiedWaitReturnsSuccessAndMapsTheConditionByKind()
    {
        var runtime = Runtime(pageVersion: 6, wait: new WaitResult { TimedOut = false });
        var result = await Surface(runtime).WaitForAsync(
            Call,
            new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.SelectorHidden, ".loading"), timeoutMs: 1_500),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.False(result.Value.TimedOut);
        Assert.Equal(6, result.Value.Page.Version.Value);

        var condition = runtime.Pages["ctx-1/p-1"].LastCondition!;
        Assert.Equal(".loading", condition.SelectorToHide);   // selector-hidden → SelectorToHide
        Assert.Null(condition.Selector);
        Assert.Equal(1_500, condition.TimeoutMs);
    }

    [Fact]
    public async Task TimeoutIsAResultNotAFailureAndStillCarriesThePageState()
    {
        var runtime = Runtime(pageVersion: 6, wait: new WaitResult { TimedOut = true, Error = "condition not met" });
        var result = await Surface(runtime).WaitForAsync(
            Call, new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, "/done")),
            CancellationToken.None);

        // 关键语义：超时不是失败（把它当异常会让上层做出错误的重试决策）。
        Assert.False(result.IsFailure);
        Assert.True(result.Value.TimedOut);
        Assert.Equal(6, result.Value.Page.Version.Value);
        Assert.Equal("condition not met", result.Value.Error);

        var condition = runtime.Pages["ctx-1/p-1"].LastCondition!;
        Assert.Equal("/done", condition.UrlPattern);
        Assert.Null(condition.SelectorToHide);
    }

    [Fact]
    public async Task PinnedVersionMismatchIsRejectedWithoutWaiting()
    {
        var runtime = Runtime(pageVersion: 6, wait: new WaitResult { TimedOut = false });
        var result = await Surface(runtime).WaitForAsync(
            Call,
            new BrowserWaitForRequest(Target,
                new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#ready"),
                expectedPageVersion: DesktopPageVersion.Require(5)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        Assert.False(runtime.Pages["ctx-1/p-1"].Waited);
    }

    [Fact]
    public async Task UnknownTargetAndNotReadyRuntimeUseTheirOwnErrorCodes()
    {
        var runtime = Runtime(pageVersion: 6, wait: new WaitResult { TimedOut = false });
        var unknown = await Surface(runtime).WaitForAsync(
            Call,
            new BrowserWaitForRequest(new DesktopPageTarget("ctx-1", "p-x"),
                new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#ready")),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, unknown.Error!.Code);

        runtime.State = BrowserRuntimeState.ShuttingDown;
        var notReady = await Surface(runtime).WaitForAsync(
            Call,
            new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#ready")),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, notReady.Error!.Code);
    }

    private static BrowserRuntimeDesktopSurface Surface(WaitFakeRuntime runtime) =>
        new(runtime, new WaitFakeTargets());

    private static WaitFakeRuntime Runtime(long pageVersion, WaitResult wait)
    {
        var runtime = new WaitFakeRuntime();
        runtime.Contexts.Add(new WaitFakeContext("ctx-1", "C:\\profile\\a", "p-1", pageVersion, wait, runtime));
        return runtime;
    }

    private sealed class WaitFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class WaitFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<WaitFakeContext> Contexts { get; } = [];

        public Dictionary<string, WaitFakePage> Pages { get; } = [];

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>(Contexts.Select(c => c.Info).ToArray());

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(Contexts.FirstOrDefault(c => c.Id.Value == id.Value));

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            AsyncEnumerable.Empty<BrowserEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitFakeContext : IBrowserContext
    {
        private readonly WaitFakePage _page;

        public WaitFakeContext(
            string id, string userDataDirectory, string pageId, long pageVersion,
            WaitResult wait, WaitFakeRuntime owner)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo { Id = Id, UserDataDirectory = userDataDirectory, PageCount = 1 };
            _page = new WaitFakePage(pageId, id, pageVersion, wait);
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

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => throw new NotSupportedException();

        public Task ClearCookiesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ResetPermissionsAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitFakePage : IBrowserPage
    {
        private readonly WaitResult _wait;

        public WaitFakePage(string id, string contextId, long pageVersion, WaitResult wait)
        {
            Id = new PageId(id);
            ContextId = new BrowserContextId(contextId);
            PageVersion = pageVersion;
            _wait = wait;
            Info = new PageInfo
            {
                Id = Id,
                ContextId = ContextId,
                Title = id,
                Url = "https://example.test/x",
                PageVersion = pageVersion,
            };
        }

        public bool Waited { get; private set; }

        public WaitCondition? LastCondition { get; private set; }

        public PageId Id { get; }

        public BrowserContextId ContextId { get; }

        public long PageVersion { get; }

        public PageInfo Info { get; }

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct)
        {
            Waited = true;
            LastCondition = condition;
            return Task.FromResult(_wait);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task BringToFrontAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<System.Text.Json.JsonDocument> SendCdpAsync(string method, System.Text.Json.JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();

        public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => throw new NotSupportedException();

        public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task ScrollAsync(ScrollOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct) => throw new NotSupportedException();

        public Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct) => throw new NotSupportedException();

        public Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}