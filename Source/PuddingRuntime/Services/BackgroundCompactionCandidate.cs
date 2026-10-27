namespace PuddingRuntime.Services;

/// <summary>
/// One execution owns one speculative candidate. Preparation only sees frozen data.
/// The foreground releases the commit gate at a request boundary and awaits completion
/// before it resumes using history. Disposal cancels and joins the worker.
/// </summary>
internal sealed class BackgroundCompactionCandidate<TBoundary, TResult> : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenSource _idle;
    private readonly CancellationToken _owner;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _idleTimeout;
    private readonly long _started;
    private long _lastProgress;
    private readonly object _sync = new();
    private CompactionSummaryAttempt? _progress;
    private int _reusedCheckpoints;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TBoundary> _boundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<TResult> Completion { get; }
    internal bool IsReady => _ready.Task.IsCompletedSuccessfully;

    internal BackgroundCompactionCandidate(
        Func<BackgroundCompactionCandidate<TBoundary, TResult>, CancellationToken, Task<TResult>> prepare,
        CancellationToken ct, TimeSpan? preparationIdleTimeout = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _idleTimeout = preparationIdleTimeout ?? TimeSpan.FromMinutes(5);
        if (_idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(preparationIdleTimeout));
        _started = _lastProgress = _clock.GetTimestamp();
        _owner = ct;
        _idle = new CancellationTokenSource(_idleTimeout, _clock);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _idle.Token);
        Completion = Task.Run(() => prepare(this, _lifetime.Token), CancellationToken.None);
    }

    internal string CancellationReason => _owner.IsCancellationRequested ? "candidate_turn_cancelled"
        : _idle.IsCancellationRequested ? "candidate_preparation_idle_timeout" : "candidate_owner_disposed";

    /// <summary>Only a complete, admitted segment renews the preparation lease; retries do not.
    /// Total work remains bounded by BoundedCompactionSummary.MaxCalls and the owning turn deadline.</summary>
    internal void ReportProgress(CompactionSummaryAttempt attempt)
    {
        lock (_sync)
        {
            _progress = attempt;
            if (attempt.ReusedCheckpoint) _reusedCheckpoints++;
            if (attempt.Decision != "accepted" || IsReady || _lifetime.IsCancellationRequested) return;
            _lastProgress = _clock.GetTimestamp();
            _idle.CancelAfter(_idleTimeout);
        }
    }

    internal IReadOnlyDictionary<string, string> GetEvidence()
    {
        lock (_sync)
        {
            static string Number(double value) => value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            return new Dictionary<string, string>
            {
                ["compaction_elapsed_ms"] = Number(_clock.GetElapsedTime(_started).TotalMilliseconds),
                ["compaction_no_progress_ms"] = Number(_clock.GetElapsedTime(_lastProgress).TotalMilliseconds),
                ["compaction_idle_timeout_ms"] = Number(_idleTimeout.TotalMilliseconds),
                ["compaction_summary_calls"] = (_progress?.Call ?? 0).ToString(),
                ["compaction_reused_checkpoints"] = _reusedCheckpoints.ToString(),
                ["compaction_summary_stage"] = _progress?.Stage ?? "preparing",
                ["compaction_summary_part"] = (_progress?.Part ?? 0).ToString(),
                ["compaction_summary_parts"] = (_progress?.PartCount ?? 0).ToString(),
                ["compaction_last_decision"] = _progress?.Decision ?? "none",
            };
        }
    }

    internal async Task<TBoundary> AwaitBoundaryAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            // A prepared candidate is waiting for its owner, not stalled in model generation.
            _idle.CancelAfter(Timeout.InfiniteTimeSpan);
            _lifetime.Token.ThrowIfCancellationRequested();
            _ready.TrySetResult();
        }
        return await _boundary.Task.WaitAsync(ct);
    }

    internal async Task WaitUntilReadyAsync(CancellationToken ct) =>
        await Task.WhenAny(_ready.Task, Completion).WaitAsync(ct);

    internal Task<TResult> CommitAtBoundaryAsync(TBoundary current)
    {
        if (!IsReady) throw new InvalidOperationException("Candidate has not reached the commit gate.");
        _lifetime.Token.ThrowIfCancellationRequested();
        _boundary.TrySetResult(current);
        return Completion;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await Completion.ConfigureAwait(false); }
        catch (Exception) { /* Completion is observed by the owner; disposal only joins. */ }
        _lifetime.Dispose();
        _idle.Dispose();
    }
}
