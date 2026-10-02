using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;
using PuddingBrowser.Abstractions;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// Bridge 侧窄端口（切片 D 第③步的一半）：把能力形状的请求翻译到既有 <see cref="IBrowserRuntime"/>。
/// 本轮只迁移了 page_state / navigate / execute_javascript，其余**必须明确拒绝**——
/// 这一批测试同时钉住"已迁移的语义"与"未迁移的响亮失败"。
/// </summary>
public sealed class BridgeBrowserCapabilitySurfaceTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desk-1"), new OperationId("op-1"), DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task PageState_ReportsLiveVersionAndOnlyHonestReadiness()
    {
        var page = new FakePage { Version = 7, IsLoading = true, Url = "https://example.test/a" };
        var surface = Create(page);

        var result = await surface.GetPageStateAsync(Target, Call);

        Assert.False(result.IsFailure);
        Assert.Equal(7, result.Value.Version.Value);
        Assert.Equal("https://example.test/a", result.Value.Url!.AbsoluteUri);
        // IsLoading 只支持"加载中/未知"两种说法：绝不声称 Interactive/Complete。
        Assert.Equal(DesktopPageReadiness.Loading, result.Value.Readiness);
    }

    [Fact]
    public async Task PageState_WithoutLiveVersion_ReportsUnknownInsteadOfFabricating()
    {
        var page = new FakePage { Version = 0, IsLoading = false, Url = "not a url" };
        var surface = Create(page);

        var result = await surface.GetPageStateAsync(Target, Call);

        Assert.False(result.IsFailure);
        Assert.False(result.Value.Version.IsKnown);
        Assert.Null(result.Value.Url);
        Assert.Equal(DesktopPageReadiness.Unknown, result.Value.Readiness);
    }

    [Fact]
    public async Task PageState_ForUnknownTarget_IsInvalidTarget()
    {
        var surface = new BridgeBrowserCapabilitySurface(new FakeRuntime(context: null));

        var result = await surface.GetPageStateAsync(Target, Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public async Task Navigate_CompletesAndCarriesThePostNavigationVersion()
    {
        var page = new FakePage
        {
            Version = 9,
            Navigate = _ => new NavigationResult { Url = new Uri("https://example.test/done"), Ok = true },
        };
        var surface = Create(page);

        var result = await surface.NavigateAsync(
            new NavigateRequest(Target, new Uri("https://example.test/a")), Call);

        Assert.False(result.IsFailure);
        Assert.Equal(NavigateDisposition.Completed, result.Value.Disposition);
        Assert.Equal(9, result.Value.PageVersion.Value);
    }

    [Fact]
    public async Task Navigate_WhenRuntimeFails_DoesNotLeakTheErrorText()
    {
        var page = new FakePage
        {
            Navigate = _ => new NavigationResult
            {
                Url = new Uri("https://example.test/a"),
                Ok = false,
                StatusCode = 502,
                ErrorText = "failed at https://example.test/a?token=SECRET",
            },
        };
        var surface = Create(page);

        var result = await surface.NavigateAsync(
            new NavigateRequest(Target, new Uri("https://example.test/a")), Call);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain("SECRET", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("502", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Javascript_OverBudget_IsTruncatedAndFlaggedWithNoHalfJson()
    {
        var page = new FakePage
        {
            Script = _ => new BrowserScriptValue
            {
                Type = "string",
                Value = JsonDocument.Parse("\"" + new string('x', 200) + "\"").RootElement.Clone(),
            },
        };
        var surface = Create(page);

        var result = await surface.ExecuteJavascriptAsync(
            new JavascriptRequest(Target, "big()", maxResultBytes: 16), Call);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.Truncated);
        Assert.Null(result.Value.JsonValue);
        Assert.Equal(JavascriptValueKind.String, result.Value.Kind);
    }

    [Theory]
    [InlineData("undefined", null, JavascriptValueKind.Undefined)]
    [InlineData("object", "null", JavascriptValueKind.Null)]
    [InlineData("boolean", null, JavascriptValueKind.Boolean)]
    [InlineData("number", null, JavascriptValueKind.Number)]
    public async Task Javascript_MapsRuntimeTypesToWireKinds(string type, string? subtype, JavascriptValueKind expected)
    {
        var page = new FakePage
        {
            Script = _ => new BrowserScriptValue
            {
                Type = type,
                Subtype = subtype,
                Value = JsonDocument.Parse("true").RootElement.Clone(),
            },
        };
        var surface = Create(page);

        var result = await surface.ExecuteJavascriptAsync(new JavascriptRequest(Target, "1"), Call);

        Assert.False(result.IsFailure);
        Assert.Equal(expected, result.Value.Kind);
    }

    [Fact]
    public async Task Contexts_SkipPagesWithoutLiveVersionAndReportHonestFlags()
    {
        var live = new FakePage { Id = new PageId("page-a"), Version = 5, Url = "https://example.test/a", CanGoBack = true };
        var versionless = new FakePage { Id = new PageId("page-b"), Version = 0, Url = "https://example.test/b" };
        var surface = Create(new FakeRuntime(contexts: [Summary("ctx-1")], context: new FakeContext(live, versionless)));

        var result = await surface.GetContextsAsync(Call);

        Assert.False(result.IsFailure);
        var context = Assert.Single(result.Value.Contexts);
        // 没有活版本的页面不进清单：契约本身也禁止（引用会失去版本依据）。
        var page = Assert.Single(context.Pages);
        Assert.Equal(5, page.Version.Value);
        Assert.True(page.CanGoBack);
        // Bridge 侧不知道这两件事：取保守值，工具不得据此做准入判断。
        Assert.False(page.IsActive);
        Assert.False(page.IsAgentTarget);
        Assert.Equal(DesktopContextTrust.Untrusted, context.Trust);
        Assert.Equal(5, result.Value.ObservedVersion.Value);
    }

    [Fact]
    public async Task Contexts_UnresolvableContext_IsSkippedInsteadOfInvented()
    {
        // 清单里有、实体取不到 ⇒ 跳过该上下文，而不是编一个空上下文出来。
        var surface = Create(new FakeRuntime(contexts: [Summary("ctx-1")], context: null));

        var result = await surface.GetContextsAsync(Call);

        Assert.False(result.IsFailure);
        Assert.Empty(result.Value.Contexts);
        Assert.False(result.Value.ObservedVersion.IsKnown);
    }

    [Fact]
    public async Task Tabs_Activate_BringsThePageToFrontAndReturnsTheRemainingList()
    {
        var page = new FakePage { Version = 3 };
        var context = new FakeContext(page);
        var surface = Create(new FakeRuntime(contexts: [Summary("ctx-1")], context: context));

        var result = await surface.TabsAsync(
            new BrowserTabsRequest(Target, DesktopTabAction.Activate, DesktopPageVersion.Require(3)), Call);

        Assert.False(result.IsFailure);
        Assert.True(page.BringToFrontCount > 0);
        Assert.False(result.Value.TabClosed);
        Assert.Equal(3, result.Value.Page.Version.Value);
        Assert.Single(result.Value.Remaining.Contexts.Single().Pages);
    }

    [Fact]
    public async Task Tabs_Close_ClosesThePageAndFlagsTheClosure()
    {
        var page = new FakePage { Version = 3 };
        var context = new FakeContext(page);
        var surface = Create(new FakeRuntime(contexts: [Summary("ctx-1")], context: context));

        var result = await surface.TabsAsync(
            new BrowserTabsRequest(Target, DesktopTabAction.Close, DesktopPageVersion.Require(3)), Call);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.TabClosed);
        Assert.Equal(1, context.ClosedPageCount);
        // 状态取自操作**之前**：关闭后页面已不存在，那时再读就不可靠了。
        Assert.Equal(3, result.Value.Page.Version.Value);
    }

    [Fact]
    public async Task OperationsNotYetMigrated_FailLoudlyInsteadOfPretending()
    {
        var surface = Create(new FakePage());

        var snapshot = await surface.SnapshotAsync(new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(1)), Call);
        var locate = await surface.LocateAsync(new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "a")), Call);
        var wait = await surface.WaitForAsync(
            new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, "/done")), Call);

        foreach (var error in new[] { snapshot.Error, locate.Error, wait.Error })
        {
            Assert.NotNull(error);
            Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, error!.Code);
        }
    }

    private static BrowserContextInfo Summary(string contextId) =>
        new() { Id = new BrowserContextId(contextId), UserDataDirectory = @"C:\udf", Persistent = true, PageCount = 1 };

    private static BridgeBrowserCapabilitySurface Create(FakePage page) => new(new FakeRuntime(page));

    private static BridgeBrowserCapabilitySurface Create(FakeRuntime runtime) => new(runtime);

    private sealed class FakeRuntime(
        IBrowserPage? page = null,
        IBrowserContext? context = null,
        IReadOnlyList<BrowserContextInfo>? contexts = null) : IBrowserRuntime
    {
        private readonly IBrowserContext? _context = context ?? (page is null ? null : new FakeContext(page));

        public BrowserRuntimeState State => BrowserRuntimeState.Ready;

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult(_context);

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult(contexts ?? []);

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeContext(params IBrowserPage[] pages) : IBrowserContext
    {
        private readonly IReadOnlyList<IBrowserPage> _pages = pages;

        public int BringToFrontCount { get; private set; }

        public int ClosedPageCount { get; private set; }

        public BrowserContextId Id { get; } = new("ctx-1");

        public BrowserContextInfo Info => throw new NotSupportedException();

        public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) =>
            Task.FromResult(_pages.FirstOrDefault(p => p.Id == id));

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PageInfo>>(_pages.Select(p => p.Info).ToArray());

        public Task ClosePageAsync(PageId id, CancellationToken ct)
        {
            ClosedPageCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) => throw new NotSupportedException();

        public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => throw new NotSupportedException();

        public Task ClearCookiesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => throw new NotSupportedException();

        public Task ResetPermissionsAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePage : IBrowserPage
    {
        public long Version { get; init; } = 1;

        public bool IsLoading { get; init; }

        public string Url { get; init; } = "https://example.test/a";

        public bool CanGoBack { get; init; }

        public bool CanGoForward { get; init; }

        public Func<Uri, NavigationResult>? Navigate { get; init; }

        public Func<BrowserScript, BrowserScriptValue>? Script { get; init; }

        public PageId Id { get; init; } = new("page-1");

        public BrowserContextId ContextId { get; } = new("ctx-1");

        /// <summary>页面被"切到前台"的次数（`BringToFront`）。</summary>
        public int BringToFrontCount { get; private set; }

        long IBrowserPage.PageVersion => Version;

        public PageInfo Info => new() { Id = Id, ContextId = ContextId, Title = "t", Url = Url, PageVersion = Version };

        bool IBrowserPage.CanGoBack => CanGoBack;

        bool IBrowserPage.CanGoForward => CanGoForward;

        bool IBrowserPage.IsLoading => IsLoading;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) =>
            Task.FromResult(Navigate?.Invoke(url) ?? new NavigationResult { Url = url, Ok = true });

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) =>
            Task.FromResult(Script?.Invoke(script) ?? new BrowserScriptValue());

        public Task BringToFrontAsync(CancellationToken ct)
        {
            BringToFrontCount++;
            return Task.CompletedTask;
        }

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
