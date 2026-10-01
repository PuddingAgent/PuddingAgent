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

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>(
                Contexts.Select(c => c.Info).ToArray());

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(
                Missing.Contains(id.Value) ? null : Contexts.FirstOrDefault(c => c.Id.Value == id.Value));

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

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct) => Task.FromResult(_pages);

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct) => throw new NotSupportedException();

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
}