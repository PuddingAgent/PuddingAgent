using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Real WebView2 browser page with full navigation support.
/// All CoreWebView2 operations are dispatched through IWebView2UiDispatcher
/// to satisfy WebView2's UI thread constraint.
/// Surface ownership: WpfBrowserSurfaceHost owns the Control; Page only unsubscribes events.
/// </summary>
public sealed class WebView2BrowserPage : IBrowserPage
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly Func<WebView2BrowserPage, Task>? _onNavigationChanged;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private CoreWebView2? _coreWebView;
    private long _pageVersion;
    private bool _disposed;

    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public IBrowserSurface? Surface { get; }

    public long PageVersion => Interlocked.Read(ref _pageVersion);
    public PageInfo Info { get; private set; }

    internal WebView2BrowserPage(
        PageId id,
        BrowserContextId contextId,
        IBrowserSurface? surface,
        IWebView2UiDispatcher dispatcher,
        Func<WebView2BrowserPage, Task>? onNavigationChanged)
    {
        Id = id;
        ContextId = contextId;
        Surface = surface;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onNavigationChanged = onNavigationChanged;

        if (surface is not null)
        {
            _coreWebView = surface.CoreWebView;
            SubscribeCoreWebViewEvents(surface.CoreWebView);
        }

        Info = new PageInfo
        {
            Id = id,
            ContextId = contextId,
            Title = string.Empty,
            Url = "about:blank",
            PageVersion = 0
        };
    }

    private void SubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.DocumentTitleChanged += OnDocumentTitleChanged;
        coreWebView.SourceChanged += OnSourceChanged;
        coreWebView.ProcessFailed += OnProcessFailed;
    }

    private void UnsubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationCompleted -= OnNavigationCompleted;
        coreWebView.DocumentTitleChanged -= OnDocumentTitleChanged;
        coreWebView.SourceChanged -= OnSourceChanged;
        coreWebView.ProcessFailed -= OnProcessFailed;
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        Info = Info with { Url = _coreWebView?.Source ?? "about:blank" };
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        Info = Info with { Title = _coreWebView?.DocumentTitle ?? string.Empty };
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Interlocked.Increment(ref _pageVersion);
        Info = Info with
        {
            Url = _coreWebView?.Source ?? Info.Url,
            Title = _coreWebView?.DocumentTitle ?? Info.Title,
            PageVersion = PageVersion
        };
        _onNavigationChanged?.Invoke(this);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // Convert to page failed state — do not crash WPF process
        Info = Info with { Title = $"[Process Failed: {e.ProcessFailedKind}]" };
        Interlocked.Increment(ref _pageVersion);
    }

    /// <summary>
    /// Navigates to the given URL. Serializes concurrent navigations per page
    /// to prevent NavigationCompleted handler cross-completion.
    /// All CoreWebView2 access goes through the UI dispatcher.
    /// </summary>
    public async Task<NavigationResult> GotoAsync(
        Uri url, NavigationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (_coreWebView is null)
            return new NavigationResult { Url = url, Ok = false, ErrorText = "No WebView" };

        await _navigationGate.WaitAsync(ct);
        try
        {
            return await _dispatcher.InvokeAsync(async () =>
            {
                var tcs = new TaskCompletionSource<NavigationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
                handler = (s, e) =>
                {
                    _coreWebView!.NavigationCompleted -= handler;
                    var sourceStr = _coreWebView.Source;
                    var resultUrl = sourceStr is not null ? new Uri(sourceStr, UriKind.RelativeOrAbsolute) : url;
                    tcs.TrySetResult(new NavigationResult
                    {
                        Url = resultUrl.IsAbsoluteUri ? resultUrl : url,
                        Ok = e.IsSuccess,
                        StatusCode = (int)e.HttpStatusCode,
                        ErrorText = e.IsSuccess ? null : e.WebErrorStatus.ToString()
                    });
                };

                _coreWebView.NavigationCompleted += handler;
                _coreWebView.Navigate(url.ToString());
                Info = Info with { Url = url.ToString() };

                var timeout = options.TimeoutMs > 0
                    ? TimeSpan.FromMilliseconds(options.TimeoutMs)
                    : TimeSpan.FromSeconds(30);

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    return await tcs.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    _coreWebView.NavigationCompleted -= handler;
                    _coreWebView.Stop();
                    return new NavigationResult { Url = url, Ok = false, ErrorText = "Navigation timeout" };
                }
                catch (OperationCanceledException)
                {
                    _coreWebView.NavigationCompleted -= handler;
                    return new NavigationResult { Url = url, Ok = false, ErrorText = "Navigation cancelled" };
                }
            }, ct);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public Task GoBackAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            if (_coreWebView?.CanGoBack == true)
                _coreWebView.GoBack();
            return Task.CompletedTask;
        }, ct);
    }

    public Task GoForwardAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            if (_coreWebView?.CanGoForward == true)
                _coreWebView.GoForward();
            return Task.CompletedTask;
        }, ct);
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.Reload();
            return Task.CompletedTask;
        }, ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.Stop();
            return Task.CompletedTask;
        }, ct);
    }

    public Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
        => throw new NotSupportedException("Snapshot not in Phase 2A-1");
    public Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task<System.Text.Json.JsonDocument> SendCdpAsync(string method, System.Text.Json.JsonElement? parameters, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task UnsubscribeAsync(BrowserSubscriptionId sid, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task ClickAsync(Locator l, ClickOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task FillAsync(Locator l, string v, FillOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task TypeAsync(Locator l, string t, TypeOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task PressAsync(Locator l, string k, KeyOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task HoverAsync(Locator l, PointerOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task ScrollAsync(ScrollOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task DragAsync(Locator s, Locator t, DragOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task SelectAsync(Locator l, IReadOnlyList<string> v, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task CheckAsync(Locator l, bool c, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task SetInputFilesAsync(Locator l, IReadOnlyList<string> p, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task<WaitResult> WaitForAsync(WaitCondition c, CancellationToken ct)
        => throw new NotSupportedException("Not in Phase 2A-1");
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions o, CancellationToken ct)
        => throw new NotSupportedException("Not in Phase 2A-1");
    public Task<PdfResult> PrintToPdfAsync(PdfOptions o, CancellationToken ct)
        => throw new NotSupportedException("Not in Phase 2A-1");

    public Task OpenDevToolsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.OpenDevToolsWindow();
            return Task.CompletedTask;
        }, ct);
    }

    /// <summary>
    /// Disposes the page. Only unsubscribes CoreWebView2 events.
    /// Surface disposal is owned by WpfBrowserSurfaceHost.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_coreWebView is not null)
        {
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    UnsubscribeCoreWebViewEvents(_coreWebView);
                    _coreWebView.Stop();
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            catch { /* best effort during dispose */ }
        }

        // Do NOT dispose Surface here — WpfBrowserSurfaceHost owns it
        _navigationGate.Dispose();
    }
}
