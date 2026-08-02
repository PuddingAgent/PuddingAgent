using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingBrowser.WebView2;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// Tests for BrowserWorkspaceController as IBrowserCommandHandler:
/// real command execution, pause/takeover gating, context/page lifecycle.
/// Uses fake IBrowserRuntime/Context/Page to avoid real WebView2.
/// </summary>
public class BrowserWorkspaceControllerTests
{
    private static BrowserBridgeCommand MakeCommand(
        string name,
        string? pageId = null,
        string? contextId = null,
        object? args = null)
    {
        return new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = name,
            PageId = pageId,
            ContextId = contextId,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Arguments = JsonSerializer.SerializeToElement(args ?? new { })
        };
    }

    [Fact]
    public async Task ExecuteAsync_ContextCreate_ReturnsDescriptor()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageCreate_ReturnsPageDescriptor()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.PageCreate, args: new { initialUrl = "https://example.com", activate = true });
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Value);

            // Verify tab was created
            Assert.Single(controller.Tabs);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageGoto_ReturnsNavigationResult()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // Create a page first
            var createCmd = MakeCommand(BrowserBridgeCommandNames.PageCreate);
            var createResult = await controller.ExecuteAsync(createCmd, CancellationToken.None);
            Assert.True(createResult.Success);

            var pageId = controller.ActivePageId!.Value.Value;

            // Navigate
            var gotoCmd = MakeCommand(BrowserBridgeCommandNames.PageGoto,
                pageId: pageId,
                args: new { url = "https://example.com", timeoutMs = 5000 });
            var gotoResult = await controller.ExecuteAsync(gotoCmd, CancellationToken.None);

            Assert.True(gotoResult.Success);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ReturnsNotSupported()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand("unknown.command");
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserOperationNotSupported, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageNotFound_ReturnsError()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.PageGetInfo, pageId: "nonexistent");
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserPageNotFound, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_WithController_SuccessPath()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_Paused_RejectsCommand()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);
        dispatcher.SetPaused(true);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_UserTakeover_RejectsCommand()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);
        dispatcher.SetUserTakeover(true);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_DuplicateOperationId_ReturnsCached()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var opId = Guid.NewGuid();
            var cmd1 = new BrowserBridgeCommand
            {
                OperationId = opId,
                Name = BrowserBridgeCommandNames.ContextCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                Arguments = JsonSerializer.SerializeToElement(new { })
            };

            var result1 = await dispatcher.DispatchAsync(cmd1, CancellationToken.None);
            Assert.True(result1.Success);

            // Same operation id again — should return cached
            var cmd2 = new BrowserBridgeCommand
            {
                OperationId = opId,
                Name = BrowserBridgeCommandNames.ContextCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                Arguments = JsonSerializer.SerializeToElement(new { })
            };
            var result2 = await dispatcher.DispatchAsync(cmd2, CancellationToken.None);

            Assert.True(result2.Success);
            Assert.Equal(result1.OperationId, result2.OperationId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TwoPages_ShareContext_DifferentPageIds()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var page1 = await controller.CreatePageAsync(null, true);
            var page2 = await controller.CreatePageAsync(null, false);

            Assert.NotEqual(page1, page2);
            Assert.Equal(2, controller.Tabs.Count);

            // Both share the same context
            Assert.NotNull(controller.ActiveContextId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CloseLastPage_KeepsContext()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var page1 = await controller.CreatePageAsync(null, true);
            Assert.NotNull(controller.ActiveContextId);

            await controller.ClosePageAsync(page1, CancellationToken.None);

            // Context should still exist (persistent)
            Assert.NotNull(controller.ActiveContextId);
            Assert.Empty(controller.Tabs);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentBrowserUdf_DiffersFrom_WorkbenchUdf()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        var agentUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "agent-browser", "user-data"));
        var workbenchUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "workbench", "user-data"));

        Assert.NotEqual(agentUdf, workbenchUdf);
    }
}

// ─── Fake implementations for testing without real WebView2 ──────────────────

internal sealed class FakeBrowserRuntime : IBrowserRuntime
{
    private readonly Dictionary<BrowserContextId, FakeBrowserContext> _contexts = new();

    public BrowserRuntimeState State { get; private set; } = BrowserRuntimeState.Created;

    public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
    {
        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var context = new FakeBrowserContext(id, options);
        _contexts[id] = context;
        State = BrowserRuntimeState.Ready;
        return Task.FromResult<IBrowserContext>(context);
    }

    public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
    {
        _contexts.TryGetValue(id, out var ctx);
        return Task.FromResult<IBrowserContext?>(ctx);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BrowserContextInfo>>(_contexts.Values.Select(c => c.Info).ToList());

    public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
    {
        _contexts.Remove(id);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        State = BrowserRuntimeState.Disposed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserContext : IBrowserContext
{
    private readonly Dictionary<PageId, FakeBrowserPage> _pages = new();

    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; private set; }

    public FakeBrowserContext(BrowserContextId id, BrowserContextOptions options)
    {
        Id = id;
        Info = new BrowserContextInfo
        {
            Id = id,
            UserDataDirectory = options.UserDataDirectory ?? Path.GetTempPath(),
            Persistent = options.Persistent,
            PageCount = 0
        };
    }

    public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct)
    {
        var pageId = new PageId(Guid.NewGuid().ToString("N"));
        var page = new FakeBrowserPage(pageId, Id);
        _pages[pageId] = page;
        Info = Info with { PageCount = _pages.Count };
        return Task.FromResult<IBrowserPage>(page);
    }

    public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
    {
        _pages.TryGetValue(id, out var page);
        return Task.FromResult<IBrowserPage?>(page);
    }

    public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PageInfo>>(_pages.Values.Select(p => p.Info).ToList());

    public Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        _pages.Remove(id);
        Info = Info with { PageCount = _pages.Count };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => Task.CompletedTask;
    public Task ClearCookiesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => Task.CompletedTask;
    public Task ResetPermissionsAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _pages.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserPage : IBrowserPage
{
    private long _version;

    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public long PageVersion => Interlocked.Read(ref _version);
    public PageInfo Info { get; private set; }

    public FakeBrowserPage(PageId id, BrowserContextId contextId)
    {
        Id = id;
        ContextId = contextId;
        Info = new PageInfo { Id = id, ContextId = contextId, Title = "Fake", Url = "about:blank", PageVersion = 0 };
    }

    public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct)
    {
        Interlocked.Increment(ref _version);
        Info = Info with { Url = url.ToString(), PageVersion = PageVersion };
        return Task.FromResult(new NavigationResult { Url = url, Ok = true, StatusCode = 200 });
    }

    public Task GoBackAsync(CancellationToken ct) => Task.CompletedTask;
    public Task GoForwardAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct) => throw new NotSupportedException();
    public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct) => throw new NotSupportedException();
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();
    public Task UnsubscribeAsync(BrowserSubscriptionId sid, CancellationToken ct) => throw new NotSupportedException();
    public Task ClickAsync(Locator l, ClickOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task FillAsync(Locator l, string v, FillOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task TypeAsync(Locator l, string t, TypeOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task PressAsync(Locator l, string k, KeyOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task HoverAsync(Locator l, PointerOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task ScrollAsync(ScrollOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task DragAsync(Locator s, Locator t, DragOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task SelectAsync(Locator l, IReadOnlyList<string> v, CancellationToken ct) => throw new NotSupportedException();
    public Task CheckAsync(Locator l, bool c, CancellationToken ct) => throw new NotSupportedException();
    public Task SetInputFilesAsync(Locator l, IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
    public Task<WaitResult> WaitForAsync(WaitCondition c, CancellationToken ct) => throw new NotSupportedException();
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task<PdfResult> PrintToPdfAsync(PdfOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task OpenDevToolsAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}


// ─── Fake SurfaceHost and UiDispatcher for Controller tests ─────────────────

internal sealed class FakeBrowserSurfaceHost : PuddingBrowser.WebView2.IBrowserSurfaceHost
{
    public List<PageId> CreatedSurfaces { get; } = new();
    public List<PageId> ActivatedSurfaces { get; } = new();
    public List<PageId> ClosedSurfaces { get; } = new();

    public Task<PuddingBrowser.WebView2.IBrowserSurface> CreateAsync(
        BrowserContextId contextId, PageId pageId,
        Microsoft.Web.WebView2.Core.CoreWebView2Environment environment,
        PageCreateOptions options, CancellationToken ct)
    {
        CreatedSurfaces.Add(pageId);
        return Task.FromResult<PuddingBrowser.WebView2.IBrowserSurface>(new FakeBrowserSurface(pageId));
    }

    public Task ActivateAsync(PageId pageId, CancellationToken ct)
    {
        ActivatedSurfaces.Add(pageId);
        return Task.CompletedTask;
    }

    public Task CloseAsync(PageId pageId, CancellationToken ct)
    {
        ClosedSurfaces.Add(pageId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeBrowserSurface : PuddingBrowser.WebView2.IBrowserSurface
{
    public FakeBrowserSurface(PageId pageId) => PageId = pageId;
    public PageId PageId { get; }
    public Microsoft.Web.WebView2.Wpf.WebView2CompositionControl Control => throw new NotSupportedException();
    public Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeUiDispatcher : PuddingBrowser.WebView2.IWebView2UiDispatcher
{
    public Task InvokeAsync(Func<Task> action, CancellationToken ct) => action();
    public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct) => action();
}
