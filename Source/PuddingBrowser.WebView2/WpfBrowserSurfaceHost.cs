using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// WPF-based browser surface host — manages WebView2 controls
/// within the BrowserWorkspaceView panel.
/// Phase 3: Stub implementation.
/// </summary>
public sealed class WpfBrowserSurfaceHost : IBrowserSurfaceHost
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly Dictionary<PageId, IBrowserSurface> _surfaces = new();

    public WpfBrowserSurfaceHost(IWebView2UiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IBrowserSurface> CreateAsync(
        BrowserContextId contextId, PageId pageId,
        CoreWebView2Environment environment, PageCreateOptions options,
        CancellationToken ct)
        => throw new NotImplementedException("Phase 3 stub");

    public Task ActivateAsync(PageId pageId, CancellationToken ct)
        => throw new NotImplementedException("Phase 3 stub");

    public Task CloseAsync(PageId pageId, CancellationToken ct)
        => throw new NotImplementedException("Phase 3 stub");
}
