namespace PuddingRuntime.Services;

/// <summary>
/// One execution owns one speculative candidate. Preparation only sees frozen data.
/// The foreground releases the commit gate at a request boundary and awaits completion
/// before it resumes using history. Disposal cancels and joins the worker.
/// </summary>
internal sealed class BackgroundCompactionCandidate<TBoundary, TResult> : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TBoundary> _boundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<TResult> Completion { get; }
    internal bool IsReady => _ready.Task.IsCompletedSuccessfully;

    internal BackgroundCompactionCandidate(
        Func<Func<CancellationToken, Task<TBoundary>>, CancellationToken, Task<TResult>> prepare,
        CancellationToken ct)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        Completion = Task.Run(() => prepare(AwaitBoundaryAsync, _lifetime.Token), CancellationToken.None);
    }

    private async Task<TBoundary> AwaitBoundaryAsync(CancellationToken ct)
    {
        _ready.TrySetResult();
        return await _boundary.Task.WaitAsync(ct);
    }

    internal async Task WaitUntilReadyAsync(CancellationToken ct) =>
        await Task.WhenAny(_ready.Task, Completion).WaitAsync(ct);

    internal Task<TResult> CommitAtBoundaryAsync(TBoundary current)
    {
        if (!IsReady) throw new InvalidOperationException("Candidate has not reached the commit gate.");
        // Preparation timeout must not cancel an already released durable transaction.
        _lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
        _boundary.TrySetResult(current);
        return Completion;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await Completion.ConfigureAwait(false); }
        catch (Exception) { /* Completion is observed by the owner; disposal only joins. */ }
        _lifetime.Dispose();
    }
}
