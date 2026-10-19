using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>
/// 截图与「可见表面」的契约（2026-10-19 缺陷修复）。
///
/// 背景：工具工作区被折叠时，WebView2 表面 <c>IsVisible=false</c>，截图必然抛
/// <c>browser_not_available</c>（“Screenshot requires a visible browser surface”）。
/// 用户期望的是「浏览器工具应该自动把面板打开」，而不是让调用方先手动展开。
///
/// 因此契约是：
/// ① 遇到该失败时，先请宿主 <see cref="IBrowserPagePresenter"/> 把页面呈现出来；
/// ② 呈现后**只重试一次**（不允许无限重试，也不允许把失败吞掉）；
/// ③ 没有呈现端口（无 UI 宿主/测试替身）时**不重试**，原样上报失败。
/// </summary>
public sealed class ScreenshotPresentationTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task HiddenSurfacePresentsThePageThenRetriesExactlyOnce()
    {
        var page = new FailingPage("p-1", "ctx-1", version: 3);
        var workspace = new PresentingWorkspace("ctx-1", "p-1");
        var surface = new BrowserRuntimeDesktopSurface(
            new OnePageRuntime(page), new UntrustedTargets(), workspace: workspace);

        var result = await surface.ScreenshotAsync(Call, Request(version: 3), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
        Assert.Equal(1, workspace.PresentCalls);   // 呈现一次
        Assert.Equal(2, page.CdpCalls);            // 首试 + 重试，各一次
    }

    [Fact]
    public async Task WithoutAPresenterTheSurfaceDoesNotRetry()
    {
        var page = new FailingPage("p-1", "ctx-1", version: 3);
        var workspace = new LifecycleOnlyWorkspace();
        var surface = new BrowserRuntimeDesktopSurface(
            new OnePageRuntime(page), new UntrustedTargets(), workspace: workspace);

        var result = await surface.ScreenshotAsync(Call, Request(version: 3), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
        Assert.Equal(1, page.CdpCalls);
    }

    [Fact]
    public async Task PresenterThatThrowsKeepsTheOriginalFailureAndDoesNotRetry()
    {
        var page = new FailingPage("p-1", "ctx-1", version: 3);
        var workspace = new PresentingWorkspace("ctx-1", "p-1") { ThrowsOnPresent = true };
        var surface = new BrowserRuntimeDesktopSurface(
            new OnePageRuntime(page), new UntrustedTargets(), workspace: workspace);

        var result = await surface.ScreenshotAsync(Call, Request(version: 3), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
        Assert.Equal(1, workspace.PresentCalls);
        Assert.Equal(1, page.CdpCalls);
    }

    private static BrowserScreenshotRequest Request(long version) => new(
        Target,
        new DesktopPageVersion(version),
        DesktopScreenshotScope.Viewport,
        DesktopScreenshotDetail.Low);

    /// <summary>每次 CDP 调用都抛「表面不可见」，用于稳定触发截图前置门。</summary>
    private sealed class FailingPage(string id, string contextId, long version) : IBrowserPage
    {
        public int CdpCalls { get; private set; }

        public PageId Id { get; } = new(id);
        public BrowserContextId ContextId { get; } = new(contextId);
        public long PageVersion { get; } = version;
        public PageInfo Info { get; } = new()
        {
            Id = new PageId(id),
            ContextId = new BrowserContextId(contextId),
            Title = "fixture",
            Url = "https://example.test/",
            PageVersion = version,
        };
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool IsLoading => false;

        public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct)
        {
            CdpCalls++;
            throw new BrowserOperationException(
                "browser_not_available",
                "Screenshot requires a visible browser surface; open the tool panel and select this browser tab.");
        }

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
        public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();
        public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScrollOutcome> ScrollAsync(ScrollOptions options, CancellationToken ct) => throw new NotSupportedException();
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

    private sealed class OnePageRuntime(IBrowserPage page) : IBrowserRuntime
    {
        private readonly PageContext _context = new(page);

        public BrowserRuntimeState State => BrowserRuntimeState.Ready;

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>([_context.Info]);

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(id.Value == page.ContextId.Value ? _context : null);

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) => EmptyEvents();

        private static async IAsyncEnumerable<BrowserEvent> EmptyEvents()
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PageContext(IBrowserPage page) : IBrowserContext
    {
        public BrowserContextId Id => page.ContextId;

        public BrowserContextInfo Info { get; } = new()
        {
            Id = page.ContextId,
            UserDataDirectory = "C:\\profile\\fixture",
            PageCount = 1,
        };

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PageInfo>>([page.Info]);

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) =>
            Task.FromResult<IBrowserPage?>(id.Value == page.Id.Value ? page : null);

        public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task ClosePageAsync(PageId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) => throw new NotSupportedException();
        public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearCookiesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => throw new NotSupportedException();
        public Task ResetPermissionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UntrustedTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.Untrusted;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        (string ContextId, string PageId)? IDesktopBrowserTargetRegistry.ActivePage => null;
    }

    /// <summary>同时提供生命周期与呈现端口：记录呈现次数，可配置为抛错。</summary>
    private sealed class PresentingWorkspace(string contextId, string pageId) : IBrowserWorkspaceLifecycle, IBrowserPagePresenter
    {
        private readonly string _contextId = contextId;
        private readonly string _pageId = pageId;

        public int PresentCalls { get; private set; }

        public bool ThrowsOnPresent { get; init; }

        public Task PresentPageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct)
        {
            PresentCalls++;
            Assert.Equal(_contextId, contextId.Value);
            Assert.Equal(_pageId, pageId.Value);
            return ThrowsOnPresent
                ? Task.FromException(new InvalidOperationException("surface cannot be presented"))
                : Task.CompletedTask;
        }

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseContextAsync(BrowserContextId contextId, CancellationToken ct) => Task.CompletedTask;
        public Task<IBrowserPage> CreatePageAsync(BrowserContextId contextId, PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task ActivatePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct) => Task.CompletedTask;
        public Task ClosePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>只有生命周期端口（模拟无 UI 的宿主/旧实现）：不应触发任何重试。</summary>
    private sealed class LifecycleOnlyWorkspace : IBrowserWorkspaceLifecycle
    {
        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseContextAsync(BrowserContextId contextId, CancellationToken ct) => Task.CompletedTask;
        public Task<IBrowserPage> CreatePageAsync(BrowserContextId contextId, PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task ActivatePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct) => Task.CompletedTask;
        public Task ClosePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct) => Task.CompletedTask;
    }
}
