using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// WebView2 browser runtime — manages browser contexts and pages using real
/// WebView2CompositionControl for airspace-compatible WPF hosting.
/// </summary>
public sealed class WebView2BrowserRuntime : IBrowserRuntime
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly IBrowserSurfaceHost? _surfaceHost;
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<BrowserContextId, WebView2BrowserContext> _contexts = new();

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

    public async Task<IBrowserContext> CreateContextAsync(
        BrowserContextOptions options,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRuntimeState.Disposed, this);
        ct.ThrowIfCancellationRequested();

        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var userDataDir = options.UserDataDirectory
            ?? Path.Combine(_dataRoot, "browser", "contexts", id.Value, "user-data");
        Directory.CreateDirectory(userDataDir);

        var environment = await _dispatcher.InvokeAsync(async () =>
        {
            return await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDir,
                options: new CoreWebView2EnvironmentOptions());
        }, ct);

        var context = new WebView2BrowserContext(id, options, environment, _dispatcher, _surfaceHost);

        if (!_contexts.TryAdd(id, context))
        {
            throw new InvalidOperationException($"Browser context already exists: {id.Value}");
        }

        State = BrowserRuntimeState.Ready;
        return context;
    }

    public Task<IBrowserContext?> GetContextAsync(
        BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _contexts.TryGetValue(id, out var context);
        return Task.FromResult(context as IBrowserContext);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var list = _contexts.Values.Select(c => c.Info).ToList();
        return Task.FromResult<IReadOnlyList<BrowserContextInfo>>(list);
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
        if (State == BrowserRuntimeState.Disposed) return;

        State = BrowserRuntimeState.ShuttingDown;
        foreach (var context in _contexts.Values)
        {
            await context.DisposeAsync();
        }
        _contexts.Clear();
        State = BrowserRuntimeState.Disposed;
    }
}
