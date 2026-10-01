using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>snapshot 映射：预算传递与纵深防御、截断如实、版本不符拒绝、无内容请求拒绝。</summary>
public sealed class SnapshotMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task SnapshotPassesTheBudgetToTheRuntimeAndKeepsThePageVersion()
    {
        var runtime = Runtime(pageVersion: 5, snapshot: new PageSnapshot
        {
            DomText = "body > div",
            AccessibilityTree = "document",
            NodeCount = 42,
            Truncated = false,
        });

        var result = await Surface(runtime).SnapshotAsync(
            Call,
            new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(5),
                new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: false, includeHtml: true,
                    maxNodes: 77, maxTextLength: 1234)),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal("body > div", result.Value.DomText);
        Assert.Equal(5, result.Value.PageVersion.Value);
        Assert.Equal(42, result.Value.NodeCount);

        var options = runtime.Pages["ctx-1/p-1"].LastOptions!;
        Assert.Equal(77, options.MaxNodes);
        Assert.Equal(1234, options.MaxTextLength);
        Assert.True(options.IncludeHtml);
        Assert.False(options.IncludeAccessibilityTree);
    }

    [Fact]
    public async Task OverBudgetContentIsTruncatedAndFlaggedEvenIfTheRuntimeIgnoresTheBudget()
    {
        var runtime = Runtime(pageVersion: 2, snapshot: new PageSnapshot
        {
            DomText = new string('x', 5_000),
            NodeCount = 3,
            Truncated = false,
        });

        var result = await Surface(runtime).SnapshotAsync(
            Call,
            new BrowserSnapshotRequest(Target, options: new DesktopSnapshotOptions(
                includeDom: true, includeAccessibilityTree: false, maxTextLength: 64)),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal(64, result.Value.DomText!.Length);
        Assert.True(result.Value.Truncated);   // 纵深防御产生的截断同样如实标注
    }

    [Fact]
    public async Task PinnedVersionMismatchIsRejectedWithoutSnapshotting()
    {
        var runtime = Runtime(pageVersion: 5, snapshot: new PageSnapshot { DomText = "x" });

        var result = await Surface(runtime).SnapshotAsync(
            Call, new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(4)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        Assert.False(runtime.Pages["ctx-1/p-1"].SnapshotTaken);
    }

    [Fact]
    public async Task RequestWithoutAnyContentKindIsRejectedUpFront()
    {
        var runtime = Runtime(pageVersion: 5, snapshot: new PageSnapshot { DomText = "x" });

        var result = await Surface(runtime).SnapshotAsync(
            Call,
            new BrowserSnapshotRequest(Target, options: new DesktopSnapshotOptions(
                includeDom: false, includeAccessibilityTree: false, includeHtml: false)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
        Assert.False(runtime.Pages["ctx-1/p-1"].SnapshotTaken);
    }

    private static BrowserRuntimeDesktopSurface Surface(SnapshotFakeRuntime runtime) =>
        new(runtime, new SnapshotFakeTargets());

    private static SnapshotFakeRuntime Runtime(long pageVersion, PageSnapshot snapshot)
    {
        var runtime = new SnapshotFakeRuntime();
        runtime.Contexts.Add(new SnapshotFakeContext("ctx-1", "C:\\profile\\a", "p-1", pageVersion, snapshot, runtime));
        return runtime;
    }

    private sealed class SnapshotFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class SnapshotFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<SnapshotFakeContext> Contexts { get; } = [];

        public Dictionary<string, SnapshotFakePage> Pages { get; } = [];

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

    private sealed class SnapshotFakeContext : IBrowserContext
    {
        private readonly SnapshotFakePage _page;

        public SnapshotFakeContext(
            string id, string userDataDirectory, string pageId, long pageVersion,
            PageSnapshot snapshot, SnapshotFakeRuntime owner)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo { Id = Id, UserDataDirectory = userDataDirectory, PageCount = 1 };
            _page = new SnapshotFakePage(pageId, id, pageVersion, snapshot);
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

    private sealed class SnapshotFakePage : IBrowserPage
    {
        private readonly PageSnapshot _snapshot;

        public SnapshotFakePage(string id, string contextId, long pageVersion, PageSnapshot snapshot)
        {
            Id = new PageId(id);
            ContextId = new BrowserContextId(contextId);
            PageVersion = pageVersion;
            _snapshot = snapshot;
            Info = new PageInfo
            {
                Id = Id,
                ContextId = ContextId,
                Title = id,
                Url = "https://example.test/x",
                PageVersion = pageVersion,
            };
        }

        public bool SnapshotTaken { get; private set; }

        public SnapshotOptions? LastOptions { get; private set; }

        public PageId Id { get; }

        public BrowserContextId ContextId { get; }

        public long PageVersion { get; }

        public PageInfo Info { get; }

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
        {
            SnapshotTaken = true;
            LastOptions = options;
            return Task.FromResult(_snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task BringToFrontAsync(CancellationToken ct) => throw new NotSupportedException();

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

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}