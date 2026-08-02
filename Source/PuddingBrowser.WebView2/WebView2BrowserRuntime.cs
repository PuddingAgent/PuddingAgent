using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// WebView2 runtime lifecycle skeleton. Context and page identities are tracked
/// in memory; browser operations remain explicit Phase 3 stubs.
/// </summary>
public sealed class WebView2BrowserRuntime : IBrowserRuntime
{
    private const string StubMessage = "Phase 3 stub";
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly IBrowserSurfaceHost? _surfaceHost;
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<BrowserContextId, StubBrowserContext> _contexts = new();

    public BrowserRuntimeState State { get; private set; } = BrowserRuntimeState.Created;

    public WebView2BrowserRuntime(
        IWebView2UiDispatcher dispatcher,
        IBrowserSurfaceHost? surfaceHost,
        string dataRoot)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surfaceHost = surfaceHost;
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
    }

    public Task<IBrowserContext> CreateContextAsync(
        BrowserContextOptions options,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRuntimeState.Disposed, this);
        ct.ThrowIfCancellationRequested();

        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var userDataDirectory = options.UserDataDirectory
            ?? Path.Combine(_dataRoot, "browser", "contexts", id.Value, "user-data");
        Directory.CreateDirectory(userDataDirectory);

        var context = new StubBrowserContext(id, options, userDataDirectory);
        if (!_contexts.TryAdd(id, context))
        {
            throw new InvalidOperationException($"Browser context already exists: {id.Value}");
        }

        State = BrowserRuntimeState.Ready;
        return Task.FromResult<IBrowserContext>(context);
    }

    public Task<IBrowserContext?> GetContextAsync(
        BrowserContextId id,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _contexts.TryGetValue(id, out var context);
        return Task.FromResult(context as IBrowserContext);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<BrowserContextInfo> contexts = _contexts.Values
            .Select(context => context.Info)
            .ToList();
        return Task.FromResult(contexts);
    }

    public async Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_contexts.TryRemove(id, out var context))
        {
            await context.DisposeAsync();
        }
    }

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    public async ValueTask DisposeAsync()
    {
        if (State == BrowserRuntimeState.Disposed)
        {
            return;
        }

        State = BrowserRuntimeState.ShuttingDown;
        foreach (var context in _contexts.Values)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
        State = BrowserRuntimeState.Disposed;
    }

    private sealed class StubBrowserContext : IBrowserContext
    {
        private readonly BrowserContextOptions _options;
        private readonly string _userDataDirectory;
        private readonly ConcurrentDictionary<PageId, StubBrowserPage> _pages = new();
        private bool _disposed;

        public StubBrowserContext(
            BrowserContextId id,
            BrowserContextOptions options,
            string userDataDirectory)
        {
            Id = id;
            _options = options;
            _userDataDirectory = userDataDirectory;
        }

        public BrowserContextId Id { get; }

        public BrowserContextInfo Info => new()
        {
            Id = Id,
            UserDataDirectory = _userDataDirectory,
            Persistent = _options.Persistent,
            PageCount = _pages.Count
        };

        public Task<IBrowserPage> NewPageAsync(
            PageCreateOptions options,
            CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();

            var id = new PageId(Guid.NewGuid().ToString("N"));
            var page = new StubBrowserPage(id, Id, options);
            if (!_pages.TryAdd(id, page))
            {
                throw new InvalidOperationException($"Browser page already exists: {id.Value}");
            }

            return Task.FromResult<IBrowserPage>(page);
        }

        public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _pages.TryGetValue(id, out var page);
            return Task.FromResult(page as IBrowserPage);
        }

        public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<PageInfo> pages = _pages.Values
                .Select(page => page.Info)
                .ToList();
            return Task.FromResult(pages);
        }

        public async Task ClosePageAsync(PageId id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_pages.TryRemove(id, out var page))
            {
                await page.DisposeAsync();
            }
        }

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
            IReadOnlyList<Uri>? urls,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task SetCookiesAsync(
            IReadOnlyList<BrowserCookie> cookies,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task ClearCookiesAsync(CancellationToken ct) =>
            throw new NotImplementedException(StubMessage);

        public Task GrantPermissionsAsync(
            Uri origin,
            IReadOnlyList<BrowserPermission> permissions,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task ResetPermissionsAsync(CancellationToken ct) =>
            throw new NotImplementedException(StubMessage);

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var page in _pages.Values)
            {
                await page.DisposeAsync();
            }

            _pages.Clear();
        }
    }

    private sealed class StubBrowserPage : IBrowserPage
    {
        private bool _disposed;

        public StubBrowserPage(
            PageId id,
            BrowserContextId contextId,
            PageCreateOptions options)
        {
            Id = id;
            ContextId = contextId;
            Info = new PageInfo
            {
                Id = id,
                ContextId = contextId,
                Title = options.Title ?? string.Empty,
                Url = options.InitialUrl?.ToString() ?? "about:blank",
                PageVersion = 0
            };
        }

        public PageId Id { get; }
        public BrowserContextId ContextId { get; }
        public long PageVersion => Info.PageVersion;
        public PageInfo Info { get; }

        public Task<NavigationResult> GotoAsync(
            Uri url,
            NavigationOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task GoBackAsync(CancellationToken ct) => throw new NotImplementedException(StubMessage);
        public Task GoForwardAsync(CancellationToken ct) => throw new NotImplementedException(StubMessage);
        public Task ReloadAsync(CancellationToken ct) => throw new NotImplementedException(StubMessage);
        public Task StopAsync(CancellationToken ct) => throw new NotImplementedException(StubMessage);
        public Task BringToFrontAsync(CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<PageSnapshot> SnapshotAsync(
            SnapshotOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<IElementHandle?> QueryAsync(
            Locator locator,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(
            Locator locator,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<BrowserScriptValue> EvaluateAsync(
            BrowserScript script,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<IJsHandle> EvaluateHandleAsync(
            BrowserScript script,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<JsonDocument> SendCdpAsync(
            string method,
            JsonElement? parameters,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<BrowserSubscriptionId> SubscribeCdpAsync(
            string eventName,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task UnsubscribeAsync(
            BrowserSubscriptionId subscriptionId,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task ClickAsync(
            Locator locator,
            ClickOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task FillAsync(
            Locator locator,
            string value,
            FillOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task TypeAsync(
            Locator locator,
            string text,
            TypeOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task PressAsync(
            Locator locator,
            string key,
            KeyOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task HoverAsync(
            Locator locator,
            PointerOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task ScrollAsync(
            ScrollOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task DragAsync(
            Locator source,
            Locator target,
            DragOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task SelectAsync(
            Locator locator,
            IReadOnlyList<string> values,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task CheckAsync(
            Locator locator,
            bool isChecked,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task SetInputFilesAsync(
            Locator locator,
            IReadOnlyList<string> paths,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<WaitResult> WaitForAsync(
            WaitCondition condition,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<ScreenshotResult> ScreenshotAsync(
            ScreenshotOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task<PdfResult> PrintToPdfAsync(
            PdfOptions options,
            CancellationToken ct) => throw new NotImplementedException(StubMessage);

        public Task OpenDevToolsAsync(CancellationToken ct) =>
            throw new NotImplementedException(StubMessage);

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
