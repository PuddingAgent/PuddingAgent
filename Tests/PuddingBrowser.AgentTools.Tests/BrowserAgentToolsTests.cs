using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools.Tests;

public sealed class BrowserAgentToolsTests
{
    [Fact]
    public void ToolDescriptors_UseStableNamesAndStructuredSchemas()
    {
        var runtime = new FakeBrowserRuntime();
        var tools = new IPuddingTool[]
        {
            new BrowserContextTool(runtime),
            new BrowserTabsTool(runtime),
            new BrowserNavigateTool(runtime),
            new BrowserSnapshotTool(runtime),
            new BrowserLocateTool(runtime),
            new BrowserInteractTool(runtime),
            new BrowserWaitForTool(runtime)
        };

        Assert.Equal(
            ["browser_context", "browser_tabs", "browser_navigate", "browser_snapshot",
                "browser_locate", "browser_interact", "browser_wait_for"],
            tools.Select(tool => tool.Descriptor.ToolId));
        Assert.All(tools.Take(3), tool => Assert.Contains("action", tool.Descriptor.Parameters.Required));
        Assert.Contains("page_id", tools[2].Descriptor.Parameters.Required);
        Assert.All(tools.Skip(3), tool => Assert.Contains("page_id", tool.Descriptor.Parameters.Required));
    }

    [Fact]
    public async Task BrowserContext_CreateListGetAndClose_UseRuntimeAbstraction()
    {
        var runtime = new FakeBrowserRuntime();
        var tool = new BrowserContextTool(runtime);

        var created = await ExecuteAsync(tool, """{"action":"create","context_id":"ctx-1"}""");
        var listed = await ExecuteAsync(tool, """{"action":"list"}""");
        var got = await ExecuteAsync(tool, """{"action":"get","context_id":"ctx-1"}""");
        var closed = await ExecuteAsync(tool, """{"action":"close","context_id":"ctx-1"}""");

        Assert.True(created.Success);
        Assert.True(listed.Success);
        Assert.True(got.Success);
        Assert.True(closed.Success);
        Assert.Empty(await runtime.ListContextsAsync(CancellationToken.None));
        AssertJsonOk(created.Output, "ctx-1");
    }

    [Fact]
    public async Task BrowserTabs_NewListActivateAndClose_ManageVisiblePages()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") },
            CancellationToken.None);
        var tool = new BrowserTabsTool(runtime);

        var created = await ExecuteAsync(tool,
            """{"action":"new","context_id":"ctx-1","url":"https://example.com"}""");
        var pageId = GetJson(created.Output, "pageId").GetString();
        var listed = await ExecuteAsync(tool, """{"action":"list","context_id":"ctx-1"}""");
        var activated = await ExecuteAsync(tool,
            $$"""{"action":"activate","context_id":"ctx-1","page_id":"{{pageId}}"}""");
        var closed = await ExecuteAsync(tool,
            $$"""{"action":"close","context_id":"ctx-1","page_id":"{{pageId}}"}""");

        Assert.True(created.Success);
        Assert.True(listed.Success);
        Assert.True(activated.Success);
        Assert.True(closed.Success);
        Assert.Equal(pageId, ((FakeBrowserContext)context).LastActivatedPageId?.Value);
        Assert.Empty(await context.ListPagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BrowserNavigate_GotoReturnsNavigationAndUpdatedPage()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") },
            CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var tool = new BrowserNavigateTool(runtime);

        var result = await ExecuteAsync(tool,
            $$"""{"action":"goto","context_id":"ctx-1","page_id":"{{page.Id.Value}}","url":"https://example.org"}""");

        Assert.True(result.Success);
        Assert.Equal("https://example.org/", page.Info.Url);
        Assert.True(GetJson(result.Output, "value").GetProperty("navigationOk").GetBoolean());
    }

    [Fact]
    public async Task InvalidAction_ReturnsStableStructuredFailure()
    {
        var result = await ExecuteAsync(
            new BrowserTabsTool(new FakeBrowserRuntime()),
            """{"action":"teleport"}""");

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("browser_invalid_arguments",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task BrowserDomainFailure_PreservesStableErrorCode()
    {
        var runtime = new FakeBrowserRuntime
        {
            Failure = new BrowserOperationException("browser_not_available", "Desktop is disconnected")
        };

        var result = await ExecuteAsync(
            new BrowserContextTool(runtime),
            """{"action":"list"}""");

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("browser_not_available",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToOrdinaryToolFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteAsync(new BrowserContextTool(new FakeBrowserRuntime()),
                """{"action":"list"}""", cts.Token));
    }

    [Fact]
    public async Task SnapshotAndLocate_ReturnVersionedRefsAndBoundedMetadata()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(new BrowserSnapshotTool(runtime),
            $$"""{"page_id":"{{page.Id.Value}}","context_id":"ctx-1","max_nodes":100}""");
        var locate = await ExecuteAsync(new BrowserLocateTool(runtime),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" }
            }));

        Assert.True(snapshot.Success);
        Assert.True(locate.Success);
        Assert.Contains("v1-n1", snapshot.Output, StringComparison.Ordinal);
        Assert.Equal("v1-n1", GetJson(locate.Output, "value").GetProperty("elements")[0].GetProperty("ref").GetString());
    }

    [Fact]
    public async Task InteractAndWait_UsePageAbstractionWithoutEchoingFillValue()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        const string secret = "do-not-echo-this-value";

        var interact = await ExecuteAsync(new BrowserInteractTool(runtime),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = secret
            }));
        var wait = await ExecuteAsync(new BrowserWaitForTool(runtime),
            $$"""{"page_id":"{{page.Id.Value}}","context_id":"ctx-1","selector":"#saved","timeout_ms":1000}""");

        Assert.True(interact.Success);
        Assert.True(wait.Success);
        Assert.Equal("fill", page.LastAction);
        Assert.Equal(secret, page.LastValue);
        Assert.DoesNotContain(secret, interact.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_StaleRefPreservesStableFailureCode()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(new BrowserLocateTool(runtime),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "ref", value = "v0-n1" }
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("stale_element_reference",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        string arguments,
        CancellationToken ct = default)
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1"
            }
        }, ct);

    private static void AssertJsonOk(string json, string expectedContextId)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedContextId, document.RootElement.GetProperty("contextId").GetString());
    }

    private static JsonElement GetJson(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).Clone();
    }
}

internal sealed class FakeBrowserRuntime : IBrowserRuntime
{
    private readonly Dictionary<BrowserContextId, FakeBrowserContext> _contexts = [];
    public BrowserOperationException? Failure { get; init; }
    public BrowserRuntimeState State { get; private set; } = BrowserRuntimeState.Created;

    public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var context = new FakeBrowserContext(id);
        _contexts[id] = context;
        State = BrowserRuntimeState.Ready;
        return Task.FromResult<IBrowserContext>(context);
    }

    public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        _contexts.TryGetValue(id, out var context);
        return Task.FromResult<IBrowserContext?>(context);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        return Task.FromResult<IReadOnlyList<BrowserContextInfo>>(
            _contexts.Values.Select(context => context.Info).ToArray());
    }

    public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        _contexts.Remove(id);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    private void ThrowIfScripted()
    {
        if (Failure is not null)
            throw Failure;
    }

    public ValueTask DisposeAsync()
    {
        State = BrowserRuntimeState.Disposed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserContext : IBrowserContext
{
    private readonly Dictionary<PageId, FakeBrowserPage> _pages = [];
    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; private set; }
    public PageId? LastActivatedPageId { get; set; }

    public FakeBrowserContext(BrowserContextId id)
    {
        Id = id;
        Info = new BrowserContextInfo
        {
            Id = id,
            UserDataDirectory = "C:/fake",
            Persistent = true,
            PageCount = 0
        };
    }

    public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = new FakeBrowserPage(
            new PageId(Guid.NewGuid().ToString("N")), Id, this);
        _pages[page.Id] = page;
        Info = Info with { PageCount = _pages.Count };
        return Task.FromResult<IBrowserPage>(page);
    }

    public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _pages.TryGetValue(id, out var page);
        return Task.FromResult<IBrowserPage?>(page);
    }

    public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PageInfo>>(_pages.Values.Select(page => page.Info).ToArray());
    }

    public Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _pages.Remove(id);
        Info = Info with { PageCount = _pages.Count };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) => Unsupported<IReadOnlyList<BrowserCookie>>();
    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => Unsupported();
    public Task ClearCookiesAsync(CancellationToken ct) => Unsupported();
    public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => Unsupported();
    public Task ResetPermissionsAsync(CancellationToken ct) => Unsupported();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Task Unsupported() => Task.FromException(new NotSupportedException());
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
}

internal sealed class FakeBrowserPage : IBrowserPage
{
    private readonly FakeBrowserContext _context;
    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public long PageVersion => Info.PageVersion;
    public PageInfo Info { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LastAction { get; private set; }
    public object? LastValue { get; private set; }

    public FakeBrowserPage(PageId id, BrowserContextId contextId, FakeBrowserContext context)
    {
        Id = id;
        ContextId = contextId;
        _context = context;
        Info = new PageInfo
        {
            Id = id,
            ContextId = contextId,
            Title = "New Tab",
            Url = "about:blank",
            PageVersion = 1
        };
    }

    public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Info = Info with { Url = url.ToString(), PageVersion = PageVersion + 1 };
        CanGoBack = true;
        return Task.FromResult(new NavigationResult { Url = url, Ok = true, StatusCode = 200 });
    }

    public Task GoBackAsync(CancellationToken ct) { CanGoBack = false; CanGoForward = true; return Task.CompletedTask; }
    public Task GoForwardAsync(CancellationToken ct) { CanGoBack = true; CanGoForward = false; return Task.CompletedTask; }
    public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) { IsLoading = false; return Task.CompletedTask; }
    public Task BringToFrontAsync(CancellationToken ct) { _context.LastActivatedPageId = Id; return Task.CompletedTask; }
    public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new PageSnapshot
        {
            DomText = "<button ref=v1-n1> Save",
            AccessibilityTree = "button ref=v1-n1 name=\"Save\"",
            NodeCount = 1
        });
    }
    public async Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => (await QueryAllAsync(locator, ct)).FirstOrDefault();
    public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (locator.Kind == LocatorKind.Ref && !locator.Value.StartsWith($"v{PageVersion}-", StringComparison.Ordinal))
            throw new BrowserOperationException("stale_element_reference", "stale ref");
        return Task.FromResult<IReadOnlyList<IElementHandle>>(
        [
            new FakeElementHandle(Id, PageVersion, new BrowserElementInfo
            {
                Ref = $"v{PageVersion}-n1",
                Tag = "button",
                Role = "button",
                Name = "Save",
                Text = "Save",
                Visible = true,
                Enabled = true,
                BoundingBox = new BoundingBox { X = 10, Y = 20, Width = 80, Height = 30 }
            })
        ]);
    }
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => Unsupported<BrowserScriptValue>();
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => Unsupported<IJsHandle>();
    public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => Unsupported<JsonDocument>();
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => Unsupported<BrowserSubscriptionId>();
    public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => Unsupported();
    public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct) => Record("click", null, ct);
    public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct) => Record("fill", value, ct);
    public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct) => Record("type", text, ct);
    public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct) => Record("press", key, ct);
    public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct) => Record("hover", null, ct);
    public Task ScrollAsync(ScrollOptions options, CancellationToken ct) => Record("scroll", null, ct);
    public Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct) => Unsupported();
    public Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct) => Record("select", values, ct);
    public Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct) => Record("check", isChecked, ct);
    public Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct) => Unsupported();
    public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new WaitResult { TimedOut = false });
    }
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => Unsupported<ScreenshotResult>();
    public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => Unsupported<PdfResult>();
    public Task OpenDevToolsAsync(CancellationToken ct) => Unsupported();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private Task Record(string action, object? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastAction = action;
        LastValue = value;
        return Task.CompletedTask;
    }
    private static Task Unsupported() => Task.FromException(new NotSupportedException());
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
}

internal sealed class FakeElementHandle : IElementHandle
{
    public FakeElementHandle(PageId pageId, long pageVersion, BrowserElementInfo info)
    {
        PageId = pageId;
        PageVersion = pageVersion;
        Info = info;
        Id = new ElementHandleId(info.Ref);
    }
    public ElementHandleId Id { get; }
    public PageId PageId { get; }
    public long PageVersion { get; }
    public int? BackendNodeId => null;
    public string LocatorFingerprint => "fake";
    public BrowserElementInfo Info { get; }
    public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => Task.FromResult(Info.BoundingBox);
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
