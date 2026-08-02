namespace PuddingBrowser.WebView2;

/// <summary>
/// WebView2 UI thread dispatcher — ensures all WebView2 and WPF control
/// access happens on the UI thread.
/// </summary>
public interface IWebView2UiDispatcher
{
    Task InvokeAsync(Func<Task> action, CancellationToken ct);
    Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct);
}

/// <summary>
/// Default WPF dispatcher-based implementation.
/// </summary>
public sealed class WpfUiDispatcher : IWebView2UiDispatcher
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public WpfUiDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task InvokeAsync(Func<Task> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_dispatcher.CheckAccess())
            return action();
        return _dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal).Task.Unwrap();
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_dispatcher.CheckAccess())
            return action();
        var tcs = new TaskCompletionSource<T>();
        _ = _dispatcher.InvokeAsync(async () =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
