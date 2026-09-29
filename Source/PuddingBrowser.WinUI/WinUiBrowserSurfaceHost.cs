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
}

public interface IBrowserSurface : IAsyncDisposable
{
    PageId PageId { get; }
    CoreWebView2 CoreWebView { get; }
}

public sealed class WinUiBrowserSurfaceHost(IWebView2UiDispatcher dispatcher, Panel container) : IBrowserSurfaceHost
{
    private readonly Dictionary<PageId, Surface> _surfaces = new();
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
                control.Visibility = Visibility.Collapsed;
                return surface;
            }
            catch { container.Children.Remove(control); control.Close(); throw; }
        }, ct);

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

    private sealed class Surface(PageId id, Microsoft.UI.Xaml.Controls.WebView2 control) : IBrowserSurface
    {
        public PageId PageId => id;
        public Microsoft.UI.Xaml.Controls.WebView2 Control => control;
        public CoreWebView2 CoreWebView => control.CoreWebView2;
        public ValueTask DisposeAsync() { control.Close(); return ValueTask.CompletedTask; }
    }
}
