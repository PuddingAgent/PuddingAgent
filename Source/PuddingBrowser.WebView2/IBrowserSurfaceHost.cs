using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Creates and manages WebView2CompositionControl surfaces within WPF.
/// Uses composition control to avoid airspace conflicts with WindowChrome/Mica.
/// </summary>
public interface IBrowserSurfaceHost
{
    Task<IBrowserSurface> CreateAsync(
        BrowserContextId contextId, PageId pageId,
        CoreWebView2Environment environment, PageCreateOptions options,
        CancellationToken ct);

    Task ActivateAsync(PageId pageId, CancellationToken ct);
    Task CloseAsync(PageId pageId, CancellationToken ct);
}

/// <summary>
/// Represents a created WebView2 surface bound to a page.
/// Uses WebView2CompositionControl for airspace compatibility.
/// </summary>
public interface IBrowserSurface : IAsyncDisposable
{
    PageId PageId { get; }
    WebView2CompositionControl Control { get; }
    CoreWebView2 CoreWebView { get; }
}
