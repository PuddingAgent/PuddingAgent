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
                Visibility = System.Windows.Visibility.Visible
            };

            _surfaceContainer.Children.Add(control);
            await control.EnsureCoreWebView2Async(environment);

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
                surface.Control.Visibility = id == pageId
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
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
}
