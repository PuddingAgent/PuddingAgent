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
    public async Task Locate_ReturnsReferencesWithLiveVersionsAndHonestFlags()
    {
        var page = new FakePage
        {
            Version = 7,
            QueryAll = _ =>
            [
                new FakeElementHandle
                {
                    Version = 7,
                    Info = new BrowserElementInfo
                    {
                        Ref = "e1", Tag = "button", Role = "button", Name = "提交", Visible = true, Enabled = false,
                    },
                },
            ],
        };
        var surface = Create(page);

        var result = await surface.LocateAsync(
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "button")), Call);

        Assert.False(result.IsFailure);
        var element = Assert.Single(result.Value.Elements);
        Assert.Equal("e1", element.Reference);
        Assert.Equal("button", element.Tag);
        Assert.Equal(7, element.PageVersion.Value);
        Assert.True(element.Visible);
        Assert.False(element.Enabled);
        Assert.Equal(7, result.Value.PageVersion.Value);
        Assert.False(result.Value.Truncated);
    }

    [Fact]
    public async Task Locate_WhenExpectedVersionDiffers_IsPageVersionMismatch()
    {
        // 与 Desktop 侧同一套语义：版本不符就明确拒绝，而不是让调用方拿到陈旧引用。
        var surface = Create(new FakePage { Version = 7 });

        var result = await surface.LocateAsync(
            new BrowserLocateRequest(
                Target, new DesktopLocator(DesktopLocatorKind.Css, "button"), DesktopPageVersion.Require(3)), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
    }

    [Fact]
    public async Task Locate_WithSnapshotReferenceLocator_MapsToTheRuntimeRefKind()
    {
        // 更正（2026-10-02）：早先判断"Ref 在 Bridge 侧无等价物"是**错的**——
        // 运行时的 LocatorKind 本身就支持 Ref，因此这里必须映射过去，而不是拒绝。
        Locator? seen = null;
        var page = new FakePage
        {
            Version = 7,
            QueryAll = locator =>
            {
                seen = locator;
                return [];
            },
        };
        var surface = Create(page);

        var result = await surface.LocateAsync(
            new BrowserLocateRequest(
                Target, new DesktopLocator(DesktopLocatorKind.Ref, "e1"), DesktopPageVersion.Require(7)), Call);

        Assert.False(result.IsFailure);
        Assert.NotNull(seen);
        Assert.Equal(LocatorKind.Ref, seen!.Kind);
        Assert.Equal("e1", seen.Value);
    }

    [Fact]
    public async Task Locate_OverMaxResults_IsTruncatedAndFlagged()
    {
        var page = new FakePage
        {
            Version = 2,
            QueryAll = _ =>
            [
                new FakeElementHandle { Version = 2, Info = new BrowserElementInfo { Ref = "e1", Tag = "a" } },
                new FakeElementHandle { Version = 2, Info = new BrowserElementInfo { Ref = "e2", Tag = "a" } },
                new FakeElementHandle { Version = 2, Info = new BrowserElementInfo { Ref = "e3", Tag = "a" } },
            ],
        };
        var surface = Create(page);

        var result = await surface.LocateAsync(
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "a"), maxResults: 1), Call);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.Truncated);
        Assert.Single(result.Value.Elements);
    }

    [Fact]
    public async Task Locate_ElementWithoutLiveVersion_FailsLoudly()
    {
        // 与 Desktop 侧同一条规则：元素没有活版本 ⇒ 响亮失败，绝不静默丢弃。
        var page = new FakePage
        {
            Version = 2,
            QueryAll = _ => [new FakeElementHandle { Version = 0, Info = new BrowserElementInfo { Ref = "e1", Tag = "a" } }],
        };
        var surface = Create(page);

        var result = await surface.LocateAsync(
            new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "a")), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
    }

    [Fact]
    public async Task WaitFor_TimeoutIsAResultNotAFailure()
    {
        var page = new FakePage { Version = 4, Wait = _ => new WaitResult { TimedOut = true } };
        var surface = Create(page);

        var result = await surface.WaitForAsync(
            new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#slow")), Call);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.TimedOut);
        Assert.Equal(4, result.Value.Page.Version.Value);
    }

    [Fact]
    public async Task WaitFor_RuntimeErrorIsAFailureWithoutLeakingTheText()
    {
        var page = new FakePage
        {
            Version = 4,
            Wait = _ => new WaitResult { Error = "timeout on #slow at https://example.test/a?token=SECRET" },
        };
        var surface = Create(page);

        var result = await surface.WaitForAsync(
            new BrowserWaitForRequest(Target, new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#slow")), Call);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain("SECRET", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interact_Click_AdvancesTheVersionAndReturnsTheNewState()
    {
        var page = new FakePage { Version = 3 };
        var surface = Create(page);

        var result = await surface.InteractAsync(
            new BrowserInteractRequest(
                Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(3),
                new DesktopLocator(DesktopLocatorKind.Css, "button")), Call);

        Assert.False(result.IsFailure);
        Assert.Equal(4, result.Value.Page.Version.Value);
        Assert.Equal(1, page.ClickCount);
    }

    [Fact]
    public async Task Interact_WithStaleExpectedVersion_IsRejectedBeforeActing()
    {
        var page = new FakePage { Version = 3 };
        var surface = Create(page);

        var result = await surface.InteractAsync(
            new BrowserInteractRequest(
                Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(1),
                new DesktopLocator(DesktopLocatorKind.Css, "button")), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        // 版本不符时**绝不能已经动过页面**：前置检查必须在动作之前。
        Assert.Equal(0, page.ClickCount);
    }

    [Fact]
    public async Task Interact_WhenVersionDoesNotAdvance_FailsLoudly()
    {
        // 变更类能力的硬不变量：结果版本必须严格推进，否则旧引用会重新"有效"。
        var page = new FakePage { Version = 3, AdvanceVersionOnClick = false };
        var surface = Create(page);

        var result = await surface.InteractAsync(
            new BrowserInteractRequest(
                Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(3),
                new DesktopLocator(DesktopLocatorKind.Css, "button")), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
        Assert.Equal(1, page.ClickCount);
    }

    [Fact]
    public async Task Interact_Focus_IsRejectedAsUnsupported()
    {
        // 运行时没有 focus API，且不拿脚本绕过注入限制 ⇒ 与 Desktop 侧同一口径：明确拒绝。
        var page = new FakePage { Version = 3 };
        var surface = Create(page);

        var result = await surface.InteractAsync(
            new BrowserInteractRequest(
                Target, DesktopInteractionAction.Focus, DesktopPageVersion.Require(3),
                new DesktopLocator(DesktopLocatorKind.Css, "input")), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, result.Error!.Code);
    }

    [Fact]
    public async Task Snapshot_MapsFieldsAndCarriesTheLiveVersion()
    {
        var page = new FakePage
        {
            Version = 6,
            Snapshot = _ => new PageSnapshot
            {
                DomText = "<button>ok</button>",
                AccessibilityTree = "button \"ok\"",
                Html = "<html/>",
                NodeCount = 3,
            },
        };
        var surface = Create(page);

        var result = await surface.SnapshotAsync(
            new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(6)), Call);

        Assert.False(result.IsFailure);
        Assert.Equal("<button>ok</button>", result.Value.DomText);
        Assert.Equal("button \"ok\"", result.Value.AccessibilityTree);
        Assert.Equal("<html/>", result.Value.Html);
        Assert.Equal(3, result.Value.NodeCount);
        Assert.False(result.Value.Truncated);
        Assert.Equal(6, result.Value.PageVersion.Value);
    }

    [Fact]
    public async Task Snapshot_WithStaleExpectedVersion_IsRejectedBeforeTouchingThePage()
    {
        // 契约：期望版本不符时**不得返回过期快照**（与 Desktop 侧同一条规则）。
        var page = new FakePage { Version = 6 };
        var surface = Create(page);

        var result = await surface.SnapshotAsync(
            new BrowserSnapshotRequest(Target, DesktopPageVersion.Require(2)), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        Assert.Equal(0, page.SnapshotCount);
    }

    [Fact]
    public async Task Snapshot_WithNoLiveVersion_FailsLoudlyWithoutSnapping()
    {
        var page = new FakePage { Version = 0 };
        var surface = Create(page);

        var result = await surface.SnapshotAsync(new BrowserSnapshotRequest(Target), Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
        Assert.Equal(0, page.SnapshotCount);
    }

    [Fact]
    public async Task Snapshot_OverBudgetText_IsClampedAndFlagged()
    {
        // 纵深防御：即使运行时漏了预算，这里也要再截一次并**如实**标注。
        var page = new FakePage
        {
            Version = 6,
            Snapshot = _ => new PageSnapshot { DomText = new string('x', 100), NodeCount = 1 },
        };
        var surface = Create(page);

        var result = await surface.SnapshotAsync(
            new BrowserSnapshotRequest(
                Target, DesktopPageVersion.Require(6), new DesktopSnapshotOptions(maxTextLength: 10)), Call);

        Assert.False(result.IsFailure);
        Assert.Equal(10, result.Value.DomText!.Length);
        Assert.True(result.Value.Truncated);
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

    private sealed class FakeElementHandle : IElementHandle
    {
        public long Version { get; init; } = 1;

        public BrowserElementInfo Info { get; init; } = new() { Ref = "e0", Tag = "div" };

        public ElementHandleId Id => new(Info.Ref);

        public PageId PageId { get; } = new("page-1");

        public int? BackendNodeId => null;

        public string LocatorFingerprint => "css=a";

        long IElementHandle.PageVersion => Version;

        public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePage : IBrowserPage
    {
        public long Version { get; set; } = 1;

        public bool IsLoading { get; init; }

        public string Url { get; init; } = "https://example.test/a";

        public bool CanGoBack { get; init; }

        public bool CanGoForward { get; init; }

        public bool AdvanceVersionOnClick { get; init; } = true;

        public int ClickCount { get; private set; }

        public Func<Uri, NavigationResult>? Navigate { get; init; }

        public Func<BrowserScript, BrowserScriptValue>? Script { get; init; }

        public Func<Locator, IReadOnlyList<IElementHandle>>? QueryAll { get; init; }

        public Func<WaitCondition, WaitResult>? Wait { get; init; }

        public Func<SnapshotOptions, PageSnapshot>? Snapshot { get; init; }

        public int SnapshotCount { get; private set; }

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

        public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
        {
            SnapshotCount++;
            return Task.FromResult(Snapshot?.Invoke(options) ?? new PageSnapshot());
        }

        public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) =>
            Task.FromResult(QueryAll?.Invoke(locator) ?? (IReadOnlyList<IElementHandle>)[]);

        public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();

        public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct) =>
            Task.FromResult(Wait?.Invoke(condition) ?? new WaitResult());

        public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct)
        {
            ClickCount++;
            if (AdvanceVersionOnClick)
            {
                Version++;
            }

            return Task.CompletedTask;
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
