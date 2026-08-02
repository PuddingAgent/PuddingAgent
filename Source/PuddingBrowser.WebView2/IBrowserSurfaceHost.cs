using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Creates and manages WebView2 surfaces within WPF.
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
/// </summary>
public interface IBrowserSurface : IAsyncDisposable
{
    PageId PageId { get; }
    Microsoft.Web.WebView2.Wpf.WebView2 Control { get; }
    CoreWebView2 CoreWebView { get; }
}
