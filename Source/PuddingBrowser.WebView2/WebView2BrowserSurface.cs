using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// WebView2CompositionControl-backed browser surface for airspace compatibility.
/// </summary>
public sealed class WebView2BrowserSurface : IBrowserSurface
{
    private readonly IDisposable _presentationGate;
    public PageId PageId { get; }
    public WebView2CompositionControl Control { get; }
    public CoreWebView2 CoreWebView => Control.CoreWebView2;

    public WebView2BrowserSurface(PageId pageId, WebView2CompositionControl control)
    {
        PageId = pageId;
        Control = control;
        _presentationGate = WebView2PresentationGate.Attach(control);
    }

    public async ValueTask DisposeAsync()
    {
        _presentationGate.Dispose();
        try
        {
            if (Control.CoreWebView2 is not null)
            {
                Control.CoreWebView2.Stop();
            }
            Control.Dispose();
        }
        catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
