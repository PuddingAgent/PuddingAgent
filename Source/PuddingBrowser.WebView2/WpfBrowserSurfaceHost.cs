using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// WPF-based browser surface host — creates and manages WebView2CompositionControl
/// instances within a WPF panel for airspace compatibility with WindowChrome/Mica.
/// </summary>
public sealed class WpfBrowserSurfaceHost : IBrowserSurfaceHost
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly System.Windows.Controls.Panel _surfaceContainer;
    private readonly Dictionary<PageId, IBrowserSurface> _surfaces = new();

    public WpfBrowserSurfaceHost(
        IWebView2UiDispatcher dispatcher,
        System.Windows.Controls.Panel surfaceContainer)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surfaceContainer = surfaceContainer ?? throw new ArgumentNullException(nameof(surfaceContainer));
    }

    public async Task<IBrowserSurface> CreateAsync(
        BrowserContextId contextId, PageId pageId,
        CoreWebView2Environment environment, PageCreateOptions options,
        CancellationToken ct)
    {
        if (_surfaces.ContainsKey(pageId))
            throw new InvalidOperationException($"Surface already exists for page: {pageId.Value}");

        var surface = await _dispatcher.InvokeAsync(async () =>
        {
            var control = new WebView2CompositionControl
            {
                // Hidden keeps the control in WPF layout so WebView2 can finish
                // initialization; Collapsed can leave EnsureCoreWebView2Async waiting.
                // The controller makes exactly one initialized surface visible.
                Visibility = System.Windows.Visibility.Hidden,
                IsHitTestVisible = false,
            };

            _surfaceContainer.Children.Add(control);
            try
            {
                await control.EnsureCoreWebView2Async(environment);
                control.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch
            {
                _surfaceContainer.Children.Remove(control);
                control.Dispose();
                throw;
            }

            return new WebView2BrowserSurface(pageId, control);
        }, ct);

        _surfaces[pageId] = surface;
        return surface;
    }

    public async Task ActivateAsync(PageId pageId, CancellationToken ct)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var (id, surface) in _surfaces)
            {
                var isActive = id == pageId;
                surface.Control.Visibility = isActive
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
                surface.Control.IsHitTestVisible = isActive;
            }
            return Task.CompletedTask;
        }, ct);
    }

    public async Task CloseAsync(PageId pageId, CancellationToken ct)
    {
        if (_surfaces.TryGetValue(pageId, out var surface))
        {
            _surfaces.Remove(pageId);
            await _dispatcher.InvokeAsync(async () =>
            {
                _surfaceContainer.Children.Remove(surface.Control);
                await surface.DisposeAsync();
            }, ct);
        }
    }

    /// <summary>
    /// 清空整个 profile 的 Cookie。
    /// 所有表面共用同一个 <see cref="CoreWebView2Environment"/>（同一个用户数据目录），
    /// 因此取任一个已初始化的表面就够，不必逐个循环 —— 与 WinUI 宿主同一语义。
    /// </summary>
    public Task<bool> ClearAllCookiesAsync(CancellationToken ct) => _dispatcher.InvokeAsync<bool>(async () =>
    {
        var core = _surfaces.Values
            .Select(surface => surface.CoreWebView)
            .FirstOrDefault(value => value is not null);
        if (core is null)
        {
            return false;
        }

        core.Profile.CookieManager.DeleteAllCookies();
        await Task.CompletedTask;
        return true;
    }, ct);

    /// <summary>
    /// 只清给定站点的 Cookie；作用域取 <c>scheme://host[:port]</c>。
    /// 深层 path 单独设置的 Cookie 可能残留（WebView2 只提供 <c>DeleteCookies(name, uri)</c>）。
    /// </summary>
    public Task<int> ClearSiteCookiesAsync(Uri siteUri, CancellationToken ct) =>
        _dispatcher.InvokeAsync<int>(async () =>
        {
            ArgumentNullException.ThrowIfNull(siteUri);
            var core = _surfaces.Values
                .Select(surface => surface.CoreWebView)
                .FirstOrDefault(value => value is not null);
            if (core is null)
            {
                return -1;
            }

            var scope = siteUri.GetLeftPart(UriPartial.Authority);
            var manager = core.Profile.CookieManager;
            var cookies = await manager.GetCookiesAsync(scope);
            foreach (var cookie in cookies)
            {
                manager.DeleteCookies(cookie.Name, scope);
            }

            return cookies.Count;
        }, ct);
}
