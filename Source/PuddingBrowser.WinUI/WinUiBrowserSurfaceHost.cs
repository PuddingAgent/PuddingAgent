using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

public interface IWebView2UiDispatcher
{
    Task InvokeAsync(Func<Task> action, CancellationToken ct);
    Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct);
}

public sealed class WinUiDispatcher(DispatcherQueue queue) : IWebView2UiDispatcher
{
    public Task InvokeAsync(Func<Task> action, CancellationToken ct) =>
        InvokeAsync(async () => { await action(); return true; }, ct);

    public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (queue.HasThreadAccess) return action();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try { ct.ThrowIfCancellationRequested(); result.TrySetResult(await action()); }
            catch (OperationCanceledException) { result.TrySetCanceled(ct); }
            catch (Exception ex) { result.TrySetException(ex); }
        })) result.TrySetException(new ObjectDisposedException(nameof(DispatcherQueue)));
        return result.Task;
    }
}

public interface IBrowserSurfaceHost
{
    Task<IBrowserSurface> CreateAsync(BrowserContextId contextId, PageId pageId,
        CoreWebView2Environment environment, PageCreateOptions options, CancellationToken ct);
    Task ActivateAsync(PageId pageId, CancellationToken ct);
    Task CloseAsync(PageId pageId, CancellationToken ct);

    /// <summary>
    /// 清空 Cookie。所有浏览器表面共用同一个 <see cref="CoreWebView2Environment"/>（同一个
    /// 用户数据目录），因此从任一表面清一次就等于整个 profile 清空；逐个表面循环只是重复劳动。
    /// 返回是否真的执行（没有已初始化的表面时为 false）。
    /// </summary>
    Task<bool> ClearAllCookiesAsync(CancellationToken ct);

    /// <summary>
    /// 只清给定站点的 Cookie，返回删除条数（无法执行时返回 -1）。
    /// 作用域按 <c>scheme://host[:port]</c> 计算，因此只覆盖该源下路径 "/" 起始的 Cookie ——
    /// 深层 path 单独设置的 Cookie 可能残留，这是 WebView2 只提供 DeleteCookies(name, uri) 的限制。
    /// </summary>
    Task<int> ClearSiteCookiesAsync(Uri siteUri, CancellationToken ct);

    /// <summary>
    /// 同步宿主外观到**每一个**浏览器表面（含之后新建的）：预绘制背景 + UA 配色。
    /// <para>
    /// 浏览器页面是独立的 WebView2，与主工作台不是同一个实例；漏掉就会出现
    /// 「浅色应用里一块纯黑画布」——UA 处于深色时 <c>about:blank</c> 的画布是
    /// Chromium 的 #121212。调用方必须在 UI 线程调用。
    /// </para>
    /// </summary>
    void ApplyAppearance(uint backgroundColorArgb, CoreWebView2PreferredColorScheme scheme);
}

public interface IBrowserSurface : IAsyncDisposable
{
    PageId PageId { get; }
    CoreWebView2 CoreWebView { get; }
    bool CanCaptureScreenshot { get; }
}

public sealed class WinUiBrowserSurfaceHost(IWebView2UiDispatcher dispatcher, Panel container) : IBrowserSurfaceHost
{
    private readonly Dictionary<PageId, Surface> _surfaces = new();

    // 最近一次宿主外观：新建表面自动继承，避免"先开浏览器再切主题"出现黑画布。
    private uint _appearanceArgb = 0xFFFFFFFF;
    private CoreWebView2PreferredColorScheme _appearanceScheme = CoreWebView2PreferredColorScheme.Auto;

    public Task<IBrowserSurface> CreateAsync(BrowserContextId contextId, PageId pageId,
        CoreWebView2Environment environment, PageCreateOptions options, CancellationToken ct) =>
        dispatcher.InvokeAsync<IBrowserSurface>(async () =>
        {
            if (_surfaces.ContainsKey(pageId)) throw new InvalidOperationException("Duplicate browser page.");
            var control = new Microsoft.UI.Xaml.Controls.WebView2();
            container.Children.Add(control);
            try
            {
                await control.EnsureCoreWebView2Async(environment);
                ct.ThrowIfCancellationRequested();
                var surface = new Surface(pageId, control);
                _surfaces.Add(pageId, surface);
                ApplyAppearance(surface);
                control.Visibility = Visibility.Collapsed;
                return surface;
            }
            catch { container.Children.Remove(control); control.Close(); throw; }
        }, ct);

    public void ApplyAppearance(uint backgroundColorArgb, CoreWebView2PreferredColorScheme scheme)
    {
        _appearanceArgb = backgroundColorArgb;
        _appearanceScheme = scheme;
        foreach (var surface in _surfaces.Values) ApplyAppearance(surface);
    }

    private void ApplyAppearance(Surface surface)
    {
        var (alpha, red, green, blue) = (
            (byte)(_appearanceArgb >> 24),
            (byte)(_appearanceArgb >> 16),
            (byte)(_appearanceArgb >> 8),
            (byte)_appearanceArgb);
        surface.Control.DefaultBackgroundColor = Windows.UI.Color.FromArgb(alpha, red, green, blue);
        if (surface.Control.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = _appearanceScheme;
    }

    public Task ActivateAsync(PageId pageId, CancellationToken ct) => dispatcher.InvokeAsync(() =>
    {
        foreach (var (id, surface) in _surfaces)
        {
            surface.Control.Visibility = id == pageId ? Visibility.Visible : Visibility.Collapsed;
            surface.Control.IsHitTestVisible = id == pageId;
        }
        return Task.CompletedTask;
    }, ct);

    public Task CloseAsync(PageId pageId, CancellationToken ct) => dispatcher.InvokeAsync(async () =>
    {
        if (_surfaces.Remove(pageId, out var surface))
        {
            container.Children.Remove(surface.Control);
            await surface.DisposeAsync();
        }
    }, ct);

    public Task<bool> ClearAllCookiesAsync(CancellationToken ct) => dispatcher.InvokeAsync<bool>(async () =>
    {
        var core = _surfaces.Values.Select(surface => surface.CoreWebView).FirstOrDefault(c => c is not null);
        if (core is null)
        {
            return false;
        }

        core.Profile.CookieManager.DeleteAllCookies();
        await Task.CompletedTask;
        return true;
    }, ct);

    public Task<int> ClearSiteCookiesAsync(Uri siteUri, CancellationToken ct) =>
        dispatcher.InvokeAsync<int>(async () =>
        {
            ArgumentNullException.ThrowIfNull(siteUri);
            var core = _surfaces.Values.Select(surface => surface.CoreWebView).FirstOrDefault(c => c is not null);
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

    private sealed class Surface(PageId id, Microsoft.UI.Xaml.Controls.WebView2 control) : IBrowserSurface
    {
        public PageId PageId => id;
        public Microsoft.UI.Xaml.Controls.WebView2 Control => control;
        public CoreWebView2 CoreWebView => control.CoreWebView2;
        public bool CanCaptureScreenshot
        {
            get
            {
                if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
                // Visibility on the WebView alone misses a collapsed tool panel or workbench.
                for (DependencyObject? node = control; node is not null;
                     node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                {
                    if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
                }
                return true;
            }
        }
        public ValueTask DisposeAsync() { control.Close(); return ValueTask.CompletedTask; }
    }
}
