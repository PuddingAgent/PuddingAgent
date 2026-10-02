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
    public async Task OperationsNotYetMigrated_FailLoudlyInsteadOfPretending()
    {
        var surface = Create(new FakePage());

        var snapshot = await surface.SnapshotAsync(new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(1)), Call);
        var locate = await surface.LocateAsync(new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "a")), Call);
        var contexts = await surface.GetContextsAsync(Call);

        foreach (var error in new[] { snapshot.Error, locate.Error, contexts.Error })
        {
            Assert.NotNull(error);
            Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, error!.Code);
        }
    }

    private static BridgeBrowserCapabilitySurface Create(FakePage page) => new(new FakeRuntime(page));

    private sealed class FakeRuntime(IBrowserPage? page = null, IBrowserContext? context = null) : IBrowserRuntime
    {
        private readonly IBrowserPage? _page = page;
        private readonly IBrowserContext? _context = context ?? (page is null ? null : new FakeContext(page));

        public BrowserRuntimeState State => BrowserRuntimeState.Ready;

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult(_context);

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeContext(IBrowserPage page) : IBrowserContext
    {
        public BrowserContextId Id { get; } = new("ctx-1");

        public BrowserContextInfo Info => throw new NotSupportedException();

        public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) => Task.FromResult<IBrowserPage?>(page);

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ClosePageAsync(PageId id, CancellationToken ct) => throw new NotSupportedException();

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

        public Func<Uri, NavigationResult>? Navigate { get; init; }

        public Func<BrowserScript, BrowserScriptValue>? Script { get; init; }

        public PageId Id { get; } = new("page-1");

        public BrowserContextId ContextId { get; } = new("ctx-1");

        long IBrowserPage.PageVersion => Version;

        public PageInfo Info => new() { Id = Id, ContextId = ContextId, Title = "t", Url = Url, PageVersion = Version };

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        bool IBrowserPage.IsLoading => IsLoading;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) =>
            Task.FromResult(Navigate?.Invoke(url) ?? new NavigationResult { Url = url, Ok = true });

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) =>
            Task.FromResult(Script?.Invoke(script) ?? new BrowserScriptValue());

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
