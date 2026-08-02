using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Real WebView2 browser context backed by a CoreWebView2Environment.
/// Pages within the same context share the same environment and user data folder.
/// Different contexts use different UDFs for complete isolation.
/// </summary>
public sealed class WebView2BrowserContext : IBrowserContext
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly IBrowserSurfaceHost? _surfaceHost;
    private readonly CoreWebView2Environment _environment;
    private readonly Dictionary<PageId, WebView2BrowserPage> _pages = new();
    private bool _disposed;

    public BrowserContextId Id { get; }
    public CoreWebView2Environment Environment => _environment;

    public BrowserContextInfo Info { get; private set; }

    public WebView2BrowserContext(
        BrowserContextId id,
        BrowserContextOptions options,
        CoreWebView2Environment environment,
        IWebView2UiDispatcher dispatcher,
        IBrowserSurfaceHost? surfaceHost)
    {
        Id = id;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surfaceHost = surfaceHost;

        Info = new BrowserContextInfo
        {
            Id = id,
            UserDataDirectory = environment.UserDataFolder,
            Persistent = options.Persistent,
            PageCount = 0
        };
    }

    public async Task<IBrowserPage> NewPageAsync(
        PageCreateOptions options,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var pageId = new PageId(Guid.NewGuid().ToString("N"));

        IBrowserSurface? surface = null;
        if (_surfaceHost is not null)
        {
            surface = await _surfaceHost.CreateAsync(Id, pageId, _environment, options, ct);
        }

        var page = new WebView2BrowserPage(pageId, Id, surface, _dispatcher, OnPageNavigationChangedAsync);
        _pages[pageId] = page;

        if (options.InitialUrl is not null)
        {
            await page.GotoAsync(options.InitialUrl, new NavigationOptions(), ct);
        }

        Info = Info with { PageCount = _pages.Count };
        return page;
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
        var list = _pages.Values.Select(p => p.Info).ToList();
        return Task.FromResult<IReadOnlyList<PageInfo>>(list);
    }

    public async Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_pages.Remove(id, out var page))
        {
            if (_surfaceHost is not null)
            {
                await _surfaceHost.CloseAsync(id, ct);
            }
            await page.DisposeAsync();
            Info = Info with { PageCount = _pages.Count };
        }
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        IReadOnlyList<Uri>? urls, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    }

    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
        => Task.CompletedTask;

    public Task ClearCookiesAsync(CancellationToken ct)
        => Task.CompletedTask;

    public Task GrantPermissionsAsync(
        Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct)
        => Task.CompletedTask;

    public Task ResetPermissionsAsync(CancellationToken ct)
        => Task.CompletedTask;

    private async Task OnPageNavigationChangedAsync(WebView2BrowserPage page)
    {
        Info = Info with { PageCount = _pages.Count };
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var pageIds = _pages.Keys.ToList();
        foreach (var id in pageIds)
        {
            await ClosePageAsync(id, CancellationToken.None);
        }
        _pages.Clear();
    }
}
