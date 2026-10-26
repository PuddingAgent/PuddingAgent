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

    /// <summary>
    /// 清空 Cookie。所有浏览器表面共用同一个 <see cref="CoreWebView2Environment"/>（同一个用户数据目录），
    /// 因此从任一表面清一次就等于整个 profile 清空；逐个表面循环只是重复劳动。
    /// 返回是否真的执行（没有已初始化的表面时为 false）。
    ///
    /// <para>
    /// ⚠️ 这个成员**曾在只有宿主副本里存在**（提交 2dc621c 把它加到了 WinUI 侧的
    /// <c>IBrowserSurfaceHost</c>），于是 <c>PuddingDesktop.WpfArchive.csproj</c> 单独编译
    /// <c>BrowserWorkspaceController</c> 时报 CS1061、连带 <c>PuddingDesktop.Tests</c> / <c>PuddingHost.Tests</c>
    /// 全部不可构建。规范接口是唯一真源 —— 新增成员必须加在这里，而不是加在同名副本上。
    /// </para>
    /// </summary>
    Task<bool> ClearAllCookiesAsync(CancellationToken ct);

    /// <summary>
    /// 只清给定站点的 Cookie，返回删除条数（无法执行时返回 -1）。
    /// 作用域按 <c>scheme://host[:port]</c> 计算，因此只覆盖该源下路径 "/" 起始的 Cookie ——
    /// 深层 path 单独设置的 Cookie 可能残留，这是 WebView2 只提供 <c>DeleteCookies(name, uri)</c> 的限制。
    /// </summary>
    Task<int> ClearSiteCookiesAsync(Uri siteUri, CancellationToken ct);
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
    /// <summary>UI-thread check: a hidden or unlaid-out surface cannot produce a compositor screenshot.</summary>
    bool CanCaptureScreenshot { get; }
}
