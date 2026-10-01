using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopSurfaceBrowserTests;

/// <summary>
/// contexts 映射：清单必须带**有效版本**（引用才有版本依据），可信级别/活动页/Agent 目标只从注册表读。
/// 用假运行时与假注册表测试 ⇒ 不需要 WebView2，可离线完整运行。
/// </summary>
public sealed class ContextsMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task ContextsCarryLiveVersionsTrustAndActivePage()
    {
        var runtime = new FakeRuntime
        {
            Contexts =
            {
                new FakeContext("ctx-1", "C:\\profile\\a",
                    Page("p-1", "ctx-1", "https://example.test/a", version: 7),
                    Page("p-2", "ctx-1", "https://example.test/b", version: 9)),
            },
        };
        var targets = new FakeTargets
        {
            Trust = { ["ctx-1"] = DesktopContextTrust.AgentAuthorized },
            AgentTargets = { ["ctx-1/p-2"] = true },
            Active = ("ctx-1", "p-2"),
        };

        var result = await new BrowserRuntimeDesktopSurface(runtime, targets).GetContextsAsync(Call, CancellationToken.None);

        Assert.False(result.IsFailure);
        var context = Assert.Single(result.Value.Contexts);
        Assert.Equal("ctx-1", context.ContextId);
        Assert.Equal(DesktopContextTrust.AgentAuthorized, context.Trust);
        Assert.Equal(2, context.PageCount);
        Assert.Equal(7, context.Pages[0].Version.Value);          // 版本来自运行时的真实值
        Assert.True(context.Pages[1].IsActive);                   // 活动页由注册表决定，不由运行时猜
        Assert.True(context.Pages[1].IsAgentTarget);
        Assert.False(context.Pages[0].IsActive);
        Assert.Equal(9, result.Value.ObservedVersion.Value);      // 取清单中的最高版本
    }

    [Fact]
    public async Task PagesWithoutALiveVersionAreNotListed()
    {
        var runtime = new FakeRuntime
        {
            Contexts =
            {
                new FakeContext("ctx-1", "C:\\profile\\a",
                    Page("p-ok", "ctx-1", "https://example.test/ok", version: 3),
                    Page("p-dead", "ctx-1", "https://example.test/dead", version: 0)),
            },
        };

        var result = await new BrowserRuntimeDesktopSurface(runtime, new FakeTargets())
            .GetContextsAsync(Call, CancellationToken.None);

        Assert.False(result.IsFailure);
        var context = Assert.Single(result.Value.Contexts);
        // 版本无效的页面不进清单：宁可少报，也不给出没有版本依据的引用。
        var page = Assert.Single(context.Pages);
        Assert.Equal("p-ok", page.Target.PageId);
    }

    [Fact]
    public async Task ContextsThatDisappearBetweenListAndGetAreSkippedNotInvented()
    {
        var runtime = new FakeRuntime
        {
            Contexts = { new FakeContext("ctx-closing", "C:\\profile\\b", Page("p-1", "ctx-closing", "https://x.test", 1)) },
            Missing = { "ctx-closing" },
        };

        var result = await new BrowserRuntimeDesktopSurface(runtime, new FakeTargets())
            .GetContextsAsync(Call, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Empty(result.Value.Contexts);
        Assert.Equal(0, result.Value.ObservedVersion.Value);
    }

    [Fact]
    public async Task RuntimeThatIsNotReadyIsReportedAsUiUnavailable()
    {
        var runtime = new FakeRuntime { State = BrowserRuntimeState.Starting };

        var result = await new BrowserRuntimeDesktopSurface(runtime, new FakeTargets())
            .GetContextsAsync(Call, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task UntrustedContextsAreReportedAsUntrustedByDefault()
    {
        var runtime = new FakeRuntime
        {
            Contexts = { new FakeContext("ctx-plain", "C:\\profile\\c", Page("p-1", "ctx-plain", "https://plain.test", 2)) },
        };

        var result = await new BrowserRuntimeDesktopSurface(runtime, new FakeTargets())
            .GetContextsAsync(Call, CancellationToken.None);

        Assert.False(result.IsFailure);
        // 未登记的上下文按最保守级别处理（fail closed）。
        Assert.Equal(DesktopContextTrust.Untrusted, Assert.Single(result.Value.Contexts).Trust);
    }

    private static PageInfo Page(string pageId, string contextId, string url, long version) => new()
    {
        Id = new PageId(pageId),
        ContextId = new BrowserContextId(contextId),
        Title = pageId,
        Url = url,
        PageVersion = version,
    };

    private sealed class FakeTargets : IDesktopBrowserTargetRegistry
    {
        public Dictionary<string, DesktopContextTrust> Trust { get; } = [];

        public Dictionary<string, bool> AgentTargets { get; } = [];

        public (string ContextId, string PageId)? Active { get; set; }

        public DesktopContextTrust TrustFor(string contextId) =>
            Trust.TryGetValue(contextId, out var trust) ? trust : DesktopContextTrust.Untrusted;

        public bool IsAgentTarget(string contextId, string pageId) =>
            AgentTargets.TryGetValue($"{contextId}/{pageId}", out var isTarget) && isTarget;

        (string ContextId, string PageId)? IDesktopBrowserTargetRegistry.ActivePage => Active;
    }

    private sealed class FakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<FakeContext> Contexts { get; } = [];

        public HashSet<string> Missing { get; } = [];

        public Dictionary<string, IBrowserPage> Pages { get; } = [];

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>(
                Contexts.Select(c => c.Info).ToArray());

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(Bind(
                Missing.Contains(id.Value) ? null : Contexts.FirstOrDefault(c => c.Id.Value == id.Value)));

        private FakeContext? Bind(FakeContext? context)
        {
            if (context is null)
            {
                return null;
            }

            var owner = this;
            context.PageResolver = pageId =>
                owner.Pages.TryGetValue($"{context.Id.Value}/{pageId.Value}", out var page) ? page : null;
            return context;
        }

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            AsyncEnumerable.Empty<BrowserEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeContext : IBrowserContext
    {
        private readonly IReadOnlyList<PageInfo> _pages;

        public FakeContext(string id, string userDataDirectory, params PageInfo[] pages)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo
            {
                Id = Id,
                UserDataDirectory = userDataDirectory,
                PageCount = pages.Length,
            };
            _pages = pages;
        }

        public BrowserContextId Id { get; }

        public BrowserContextInfo Info { get; }

        /// <summary>由 FakeRuntime 注入：按 PageId 返回该上下文中的页（保持测试里 runtime.Pages 的写法）。</summary>
        public Func<PageId, IBrowserPage?>? PageResolver { get; set; }

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) => Task.FromResult(_pages);

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) =>
            Task.FromResult(PageResolver?.Invoke(id));

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

/// <summary>page_state 映射：目标不存在 ⇒ invalid_target；就绪度与版本缺失都**如实**表达，不假装。</summary>
public sealed class PageStateMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task PageStateReportsUrlLiveVersionAndLoadingHonestly()
    {
        var surface = Surface(new FakeRuntime
        {
            Contexts = { new FakeContext("ctx-1", "C:\\profile\\a", Page("p-1", "ctx-1", "https://example.test/x", 7)) },
            Pages = { ["ctx-1/p-1"] = new FakePage("p-1", "ctx-1", "https://example.test/x", version: 7, isLoading: true) },
        });

        var result = await surface.GetPageStateAsync(Call, Target, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal(new Uri("https://example.test/x"), result.Value.Url);
        Assert.Equal(7, result.Value.Version.Value);
        Assert.Equal(DesktopPageReadiness.Loading, result.Value.Readiness);
        Assert.Equal(Target, result.Value.Target);
    }

    [Fact]
    public async Task WithoutALiveVersionTheStateSaysUnknownInsteadOfFakingOne()
    {
        var surface = Surface(new FakeRuntime
        {
            Contexts = { new FakeContext("ctx-1", "C:\\profile\\a", Page("p-1", "ctx-1", "https://example.test/x", 0)) },
            Pages = { ["ctx-1/p-1"] = new FakePage("p-1", "ctx-1", "https://example.test/x", version: 0, isLoading: false) },
        });

        var result = await surface.GetPageStateAsync(Call, Target, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.False(result.Value.Version.IsKnown);
        // 就绪度也只能报 Unknown：既有抽象无法区分 Interactive/Complete（需要就绪请用 wait_for）。
        Assert.Equal(DesktopPageReadiness.Unknown, result.Value.Readiness);
    }

    [Fact]
    public async Task UnknownContextOrPageIsAnInvalidTarget()
    {
        var surface = Surface(new FakeRuntime
        {
            Contexts = { new FakeContext("ctx-1", "C:\\profile\\a") },
        });

        var unknownContext = await surface.GetPageStateAsync(
            Call, new DesktopPageTarget("ctx-other", "p-1"), CancellationToken.None);
        Assert.True(unknownContext.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, unknownContext.Error!.Code);

        var unknownPage = await surface.GetPageStateAsync(Call, Target, CancellationToken.None);
        Assert.True(unknownPage.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, unknownPage.Error!.Code);
    }

    [Fact]
    public async Task NotReadyRuntimeIsUiUnavailable()
    {
        var surface = Surface(new FakeRuntime { State = BrowserRuntimeState.Created });

        var result = await surface.GetPageStateAsync(Call, Target, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
    }

    private static BrowserRuntimeDesktopSurface Surface(FakeRuntime runtime) =>
        new(runtime, new FakeTargets());

    private static PageInfo Page(string pageId, string contextId, string url, long version) => new()
    {
        Id = new PageId(pageId),
        ContextId = new BrowserContextId(contextId),
        Title = pageId,
        Url = url,
        PageVersion = version,
    };

    private sealed class FakePage(string id, string contextId, string url, long version, bool isLoading) : IBrowserPage
    {
        public PageId Id { get; } = new(id);

        public BrowserContextId ContextId { get; } = new(contextId);

        public long PageVersion { get; } = version;

        public PageInfo Info { get; } = Page(id, contextId, url, version);

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading { get; } = isLoading;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url2, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

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

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

}

/// <summary>tabs 映射：变更类必须固定版本（版本不符即拒绝且**不执行动作**），操作后回带活动页与剩余清单。</summary>
public sealed class TabsMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task ActivateBringsThePinnedPageToFrontAndKeepsTheContexts()
    {
        var runtime = Runtime(recordBringToFront: true);
        var result = await Surface(runtime).TabsAsync(
            Call, new BrowserTabsRequest(Target, DesktopTabAction.Activate, DesktopPageVersion.Require(7)), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.False(result.Value.TabClosed);
        Assert.Equal("p-1", result.Value.Page.Target.PageId);
        Assert.Equal(7, result.Value.Page.Version.Value);
        Assert.Single(result.Value.Remaining.Contexts);
        Assert.Equal(2, result.Value.Remaining.PageCount);
        Assert.True(runtime.Pages["ctx-1/p-1"].BroughtToFront);
    }

    [Fact]
    public async Task AVersionsMismatchIsRejectedWithoutPerformingTheAction()
    {
        var runtime = Runtime(recordBringToFront: true);
        var result = await Surface(runtime).TabsAsync(
            Call, new BrowserTabsRequest(Target, DesktopTabAction.Activate, DesktopPageVersion.Require(6)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        // 关键：版本不符时**绝不**执行动作（否则会切换/关闭另一个页面）。
        Assert.False(runtime.Pages["ctx-1/p-1"].BroughtToFront);
        Assert.Empty(runtime.ClosedPages);
    }

    [Fact]
    public async Task CloseRemovesThePageAndReportsWhatRemains()
    {
        var runtime = Runtime();
        var result = await Surface(runtime).TabsAsync(
            Call, new BrowserTabsRequest(Target, DesktopTabAction.Close, DesktopPageVersion.Require(7)), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.TabClosed);
        Assert.Contains("ctx-1/p-1", runtime.ClosedPages);
        Assert.Equal(1, result.Value.Remaining.PageCount);
    }

    [Fact]
    public async Task UnknownTargetsAndNotReadyRuntimeAreReportedAsSuch()
    {
        var runtime = Runtime();
        var unknownContext = await Surface(runtime).TabsAsync(
            Call,
            new BrowserTabsRequest(new DesktopPageTarget("ctx-x", "p-1"), DesktopTabAction.Activate, DesktopPageVersion.Require(7)),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, unknownContext.Error!.Code);

        runtime.State = BrowserRuntimeState.Starting;
        var notReady = await Surface(runtime).TabsAsync(
            Call, new BrowserTabsRequest(Target, DesktopTabAction.Activate, DesktopPageVersion.Require(7)), CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, notReady.Error!.Code);
    }

    private static BrowserRuntimeDesktopSurface Surface(TabsFakeRuntime runtime) =>
        new(runtime, new TabsFakeTargets());

    private static TabsFakeRuntime Runtime(bool recordBringToFront = false)
    {
        var runtime = new TabsFakeRuntime
        {
            Contexts =
            {
                new TabsFakeContext("ctx-1", "C:\\profile\\a",
                    Info("p-1", "ctx-1", "https://example.test/a", 7),
                    Info("p-2", "ctx-1", "https://example.test/b", 8)),
            },
        };
        runtime.Pages["ctx-1/p-1"] = new TabsFakePage("p-1", "ctx-1", "https://example.test/a", 7)
        {
            RecordBringToFront = recordBringToFront,
        };
        runtime.Pages["ctx-1/p-2"] = new TabsFakePage("p-2", "ctx-1", "https://example.test/b", 8);
        return runtime;
    }

    private static PageInfo Info(string pageId, string contextId, string url, long version) => new()
    {
        Id = new PageId(pageId),
        ContextId = new BrowserContextId(contextId),
        Title = pageId,
        Url = url,
        PageVersion = version,
    };

    private sealed class TabsFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class TabsFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<TabsFakeContext> Contexts { get; } = [];

        public Dictionary<string, TabsFakePage> Pages { get; } = [];

        public HashSet<string> ClosedPages { get; } = [];

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>(Contexts.Select(c => c.Info).ToArray());

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
        {
            var context = Contexts.FirstOrDefault(c => c.Id.Value == id.Value);
            if (context is not null)
            {
                context.Owner = this;
            }

            return Task.FromResult<IBrowserContext?>(context);
        }

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            AsyncEnumerable.Empty<BrowserEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TabsFakeContext : IBrowserContext
    {
        private readonly List<PageInfo> _pages;

        public TabsFakeContext(string id, string userDataDirectory, params PageInfo[] pages)
        {
            Id = new BrowserContextId(id);
            _pages = [.. pages];
            Info = new BrowserContextInfo
            {
                Id = Id,
                UserDataDirectory = userDataDirectory,
                PageCount = pages.Length,
            };
        }

        public TabsFakeRuntime? Owner { get; set; }

        public BrowserContextId Id { get; }

        public BrowserContextInfo Info { get; private set; }

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PageInfo>>(_pages.ToArray());

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) =>
            Task.FromResult<IBrowserPage?>(
                Owner is not null && Owner.Pages.TryGetValue($"{Id.Value}/{id.Value}", out var page) ? page : null);

        public Task ClosePageAsync(PageId id, CancellationToken ct)
        {
            var key = $"{Id.Value}/{id.Value}";
            _pages.RemoveAll(page => page.Id.Value == id.Value);
            Owner?.ClosedPages.Add(key);
            Owner?.Pages.Remove(key);
            Info = Info with { PageCount = _pages.Count };
            return Task.CompletedTask;
        }

        public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => throw new NotSupportedException();

        public Task ClearCookiesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ResetPermissionsAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TabsFakePage(string id, string contextId, string url, long version) : IBrowserPage
    {
        public bool RecordBringToFront { get; set; }

        public bool BroughtToFront { get; private set; }

        public PageId Id { get; } = new(id);

        public BrowserContextId ContextId { get; } = new(contextId);

        public long PageVersion { get; } = version;

        public PageInfo Info { get; } = Info(id, contextId, url, version);

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task BringToFrontAsync(CancellationToken ct)
        {
            if (RecordBringToFront)
            {
                BroughtToFront = true;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url2, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

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

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task OpenDevToolsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

/// <summary>
/// locate 映射：①凭 Ref 定位必须核对来源版本（不符即拒绝且不查询）；
/// ②**每个 Ref 的版本取元素自身**，绝不替换成页面当前版本；③命中 0 不是错误；④被裁剪如实标注。
/// </summary>
public sealed class LocateMappingTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    [Fact]
    public async Task MatchingReferenceVersionReturnsTheElementWithItsOwnVersion()
    {
        // 页面在 v9，但元素自称来自 v8：映射必须用**元素自身**的版本（这正是曾经失效的那条不变量）。
        var runtime = Runtime(pageVersion: 9, handles: [Handle("e1", "button", version: 8)]);
        var result = await Surface(runtime).LocateAsync(
            Call,
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Ref, "e1"), DesktopPageVersion.Require(9)),
            CancellationToken.None);

        Assert.False(result.IsFailure);
        var element = Assert.Single(result.Value.Elements);
        Assert.Equal(8, element.PageVersion.Value);
        Assert.Equal(9, result.Value.PageVersion.Value);
        Assert.False(result.Value.Truncated);
    }

    [Fact]
    public async Task StaleReferenceIsRejectedWithoutQuerying()
    {
        var runtime = Runtime(pageVersion: 9, handles: [Handle("e1", "button", version: 9)]);
        var result = await Surface(runtime).LocateAsync(
            Call,
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Ref, "e1"), DesktopPageVersion.Require(7)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        // 关键：版本不符时**绝不查询**（否则可能命中另一个页面的元素）。
        Assert.False(runtime.Pages["ctx-1/p-1"].Queried);
    }

    [Fact]
    public async Task EmptyResultIsSuccessAndTruncationIsReported()
    {
        var empty = await Surface(Runtime(pageVersion: 3, handles: [])).LocateAsync(
            Call, new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, ".none")), CancellationToken.None);
        Assert.False(empty.IsFailure);
        Assert.True(empty.Value.IsEmpty);

        var many = Runtime(pageVersion: 3, handles: [.. Enumerable.Range(1, 25).Select(index => Handle($"e{index}", "div", 3))]);
        var truncated = await Surface(many).LocateAsync(
            Call,
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "div"), maxResults: 20),
            CancellationToken.None);

        Assert.False(truncated.IsFailure);
        Assert.Equal(20, truncated.Value.Elements.Count);
        Assert.True(truncated.Value.Truncated);
    }

    [Fact]
    public async Task ElementsWithoutALiveVersionFailLoudlyInsteadOfBeingDropped()
    {
        var runtime = Runtime(pageVersion: 4, handles: [Handle("e1", "button", version: 0)]);
        var result = await Surface(runtime).LocateAsync(
            Call, new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "button")), CancellationToken.None);

        // 静默丢弃命中项会隐藏真相；这里必须响亮报错。
        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
    }

    [Fact]
    public async Task UnknownTargetAndNotReadyRuntimeAreReportedAsSuch()
    {
        var runtime = Runtime(pageVersion: 4, handles: []);
        var unknown = await Surface(runtime).LocateAsync(
            Call,
            new BrowserLocateRequest(new DesktopPageTarget("ctx-1", "p-x"), new DesktopLocator(DesktopLocatorKind.Css, "a")),
            CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, unknown.Error!.Code);

        runtime.State = BrowserRuntimeState.ShuttingDown;
        var notReady = await Surface(runtime).LocateAsync(
            Call, new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "a")), CancellationToken.None);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, notReady.Error!.Code);
    }

    private static BrowserRuntimeDesktopSurface Surface(LocateFakeRuntime runtime) =>
        new(runtime, new LocateFakeTargets());

    private static BrowserElementInfo Info(string reference, string tag) => new()
    {
        Ref = reference,
        Tag = tag,
        Visible = true,
        Enabled = true,
    };

    private static LocateFakeHandle Handle(string reference, string tag, long version) =>
        new(reference, tag, version);

    private static LocateFakeRuntime Runtime(long pageVersion, IReadOnlyList<LocateFakeHandle> handles)
    {
        var runtime = new LocateFakeRuntime();
        runtime.Contexts.Add(new LocateFakeContext("ctx-1", "C:\\profile\\a", "p-1", pageVersion, handles, runtime));
        return runtime;
    }

    private sealed class LocateFakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class LocateFakeHandle(string reference, string tag, long version) : IElementHandle
    {
        public ElementHandleId Id { get; } = new(reference);

        public PageId PageId { get; } = new("p-1");

        public long PageVersion { get; } = version;

        public int? BackendNodeId => 1;

        public string LocatorFingerprint => reference;

        public BrowserElementInfo Info { get; } = Info(reference, tag);

        public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => Task.FromResult<BoundingBox?>(null);

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LocateFakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Ready;

        public List<LocateFakeContext> Contexts { get; } = [];

        public Dictionary<string, LocateFakePage> Pages { get; } = [];

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

    private sealed class LocateFakeContext : IBrowserContext
    {
        private readonly LocateFakePage _page;

        public LocateFakeContext(
            string id, string userDataDirectory, string pageId, long pageVersion,
            IReadOnlyList<LocateFakeHandle> handles, LocateFakeRuntime owner)
        {
            Id = new BrowserContextId(id);
            Info = new BrowserContextInfo { Id = Id, UserDataDirectory = userDataDirectory, PageCount = 1 };
            _page = new LocateFakePage(pageId, id, pageVersion, handles);
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

    private sealed class LocateFakePage : IBrowserPage
    {
        private readonly IReadOnlyList<LocateFakeHandle> _handles;

        public LocateFakePage(string id, string contextId, long version, IReadOnlyList<LocateFakeHandle> handles)
        {
            Id = new PageId(id);
            ContextId = new BrowserContextId(contextId);
            PageVersion = version;
            _handles = handles;
            Info = new PageInfo
            {
                Id = Id,
                ContextId = ContextId,
                Title = id,
                Url = "https://example.test/x",
                PageVersion = version,
            };
        }

        public bool Queried { get; private set; }

        public PageId Id { get; }

        public BrowserContextId ContextId { get; }

        public long PageVersion { get; }

        public PageInfo Info { get; }

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading => false;

        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        {
            Queried = true;
            return Task.FromResult<IElementHandle?>(_handles.FirstOrDefault());
        }

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
        {
            Queried = true;
            return Task.FromResult<IReadOnlyList<IElementHandle>>(_handles.Cast<IElementHandle>().ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task GoBackAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task GoForwardAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task BringToFrontAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();

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
}}