using Microsoft.UI.Dispatching;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Kernel;

internal sealed class WinUiDesktopServices(DispatcherQueue queue, Action<ShellPage> show, Action<WorkspaceDocument> open)
    : IDesktopServices, IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    public Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default)
        => DispatchAsync(() => show(page), cancellationToken);
    public Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
        => DispatchAsync(() => open(document), cancellationToken);

    private async Task DispatchAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        linked.Token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
        if (!queue.TryEnqueue(() =>
        {
            if (completion.Task.IsCompleted) return;
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) throw new InvalidOperationException("Desktop dispatcher is shutting down.");
        await completion.Task.ConfigureAwait(false);
    }
    public void Dispose() => _closed.Cancel();
}
