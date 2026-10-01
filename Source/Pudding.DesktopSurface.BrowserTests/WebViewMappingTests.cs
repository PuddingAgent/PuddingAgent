using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>
/// navigate/javascript 映射：导航失败映射成可判定的目标错误（不是 internal）；
/// 脚本返回**裸 JSON 片段**；超预算时只标注截断而不给半截 JSON。
/// </summary>
public sealed class WebViewMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task SuccessfulNavigationReportsCompletedAndTheNewVersion()
    {
        var runtime = Runtime(pageVersion: 6, advanceTo: 7, navigation: new NavigationResult
        {
            Url = new Uri("https://example.test/target"),
            Ok = true,
            StatusCode = 200,
        });

        var result = await Surface(runtime).NavigateAsync(
            Call, new NavigateRequest(Target, new Uri("https://example.test/target"), DesktopPageVersion.Require(6)),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal(NavigateDisposition.Completed, result.Value.Disposition);
        Assert.Equal(new Uri("https://example.test/target"), result.Value.CurrentUrl);
        Assert.Equal(7, result.Value.PageVersion.Value);
    }

    [Fact]
    public async Task FailedNavigationIsATargetErrorNotAnInternalError()
    {
        var runtime = Runtime(pageVersion: 6, advanceTo: 7, navigation: new NavigationResult
        {
            Url = new Uri("https://example.test/target"),
            Ok = false,
            StatusCode = 502,
        });

        var result = await Surface(runtime).NavigateAsync(
            Call, new NavigateRequest(Target, new Uri("https://example.test/target")), CancellationToken.None);

        Assert.True(result.IsFailure);
        // 上层必须能区分"这个地址去不了"与"运行时坏了"。
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, result.Error!.Code);
        Assert.Contains("502", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedVersionMismatchStopsTheNavigation()
    {
        var runtime = Runtime(pageVersion: 6, advanceTo: 7, navigation: new NavigationResult
        {
            Url = new Uri("https://example.test/target"),
            Ok = true,
        });

        var result = await Surface(runtime).NavigateAsync(
            Call, new NavigateRequest(Target, new Uri("https://example.test/target"), DesktopPageVersion.Require(3)),
            CancellationToken.None);

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        Assert.False(runtime.Pages["ctx-1/p-1"].Navigated);
    }

    [Fact]
    public async Task ScriptValuesComeBackAsBareJsonFragments()
    {
        var runtime = Runtime(pageVersion: 6, advanceTo: 6, script: new BrowserScriptValue
        {
            Type = "string",
            Value = JsonSerializer.SerializeToElement("hi"),
        });

        var result = await Surface(runtime).ExecuteJavascriptAsync(
            Call, new JavascriptRequest(Target, "document.title"), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal(JavascriptValueKind.String, result.Value.Kind);
        Assert.Equal("\"hi\"", result.Value.JsonValue);   // 裸 JSON 片段：带引号，调用方无需二次编码
        Assert.False(result.Value.Truncated);
    }

    [Fact]
    public async Task UndefinedAndObjectsAreMappedHonestly()
    {
        var undefined = Runtime(pageVersion: 6, advanceTo: 6, script: new BrowserScriptValue { Type = "undefined" });
        var undefinedResult = await Surface(undefined).ExecuteJavascriptAsync(
            Call, new JavascriptRequest(Target, "void 0"), CancellationToken.None);
        Assert.Equal(JavascriptValueKind.Undefined, undefinedResult.Value.Kind);
        Assert.Null(undefinedResult.Value.JsonValue);

        var objectRuntime = Runtime(pageVersion: 6, advanceTo: 6, script: new BrowserScriptValue
        {
            Type = "object",
            Value = JsonSerializer.SerializeToElement(new { a = 1 }),
        });
        var objectResult = await Surface(objectRuntime).ExecuteJavascriptAsync(
            Call, new JavascriptRequest(Target, "({a:1})"), CancellationToken.None);
        Assert.Equal(JavascriptValueKind.Json, objectResult.Value.Kind);
        Assert.Equal("{\"a\":1}", objectResult.Value.JsonValue);
    }

    [Fact]
    public async Task OverBudgetResultsAreFlaggedWithoutHandingBackBrokenJson()
    {
        var runtime = Runtime(pageVersion: 6, advanceTo: 6, script: new BrowserScriptValue
        {
            Type = "object",
            Value = JsonSerializer.SerializeToElement(new { big = new string('x', 500) }),
        });

        var result = await Surface(runtime).ExecuteJavascriptAsync(
            Call, new JavascriptRequest(Target, "big()", maxResultBytes: 32), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.Truncated);
        // 半截 JSON 无法解析且原因不明 ⇒ 只标注截断，不返回值。
        Assert.Null(result.Value.JsonValue);
    }

    private static BrowserRuntimeDesktopSurface Surface(WebViewFakeRuntime runtime) =>
        new(runtime, new WebViewFakeTargets());

    private static WebViewFakeRuntime Runtime(long pageVersion, long advanceTo, NavigationResult? navigation = null, BrowserScriptValue? script = null)
    {
        var runtime = new WebViewFakeRuntime();
        runtime.Contexts.Add(new WebViewFakeContext("ctx-1", "p-1", pageVersion, advanceTo, navigation, script, runtime));
        return runtime;
    }

    private sealed class WebViewFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class WebViewFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<WebViewFakeContext> Contexts { get; } = [];

        public Dictionary<string, WebViewFakePage> Pages { get; } = [];

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

    private sealed class WebViewFakeContext : IBrowserContext
    {
        private readonly WebViewFakePage _page;

        public WebViewFakeContext(
            string id, string pageId, long pageVersion, long advanceTo,
            NavigationResult? navigation, BrowserScriptValue? script, WebViewFakeRuntime owner)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo { Id = Id, UserDataDirectory = "C:\\profile\\a", PageCount = 1 };
            _page = new WebViewFakePage(pageId, id, pageVersion, advanceTo, navigation, script);
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

    private sealed class WebViewFakePage : IBrowserPage
    {
        private readonly NavigationResult? _navigation;

        private readonly BrowserScriptValue? _script;

        public WebViewFakePage(
            string id, string contextId, long pageVersion, long advanceTo,
            NavigationResult? navigation, BrowserScriptValue? script)
        {
            Id = new PageId(id);
            ContextId = new BrowserContextId(contextId);
            PageVersion = pageVersion;
            AdvanceTo = advanceTo;
            _navigation = navigation;
            _script = script;
            Info = new PageInfo { Id = Id, ContextId = ContextId, Title = id, Url = "https://example.test/start", PageVersion = pageVersion };
        }

        public long AdvanceTo { get; }

        public bool Navigated { get; private set; }

        public PageId Id { get; }

        public BrowserContextId ContextId { get; }

        public long PageVersion { get; private set; }

        public PageInfo Info { get; private set; }

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct)
        {
            Navigated = true;
            PageVersion = AdvanceTo;
            Info = Info with { PageVersion = AdvanceTo, Url = url.ToString() };
            return Task.FromResult(_navigation!);
        }

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) =>
            Task.FromResult(_script!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task BringToFrontAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();

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

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}