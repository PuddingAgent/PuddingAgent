using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PuddingCodeIntelligence.Services.CodeIndex;

/// <summary>
/// Collapses scattered <see cref="IndexChange"/> observations into batches.
/// <para>
/// Debounce: a batch is produced once the tree has been quiet for <see cref="DefaultSilenceWindow"/>
/// (silence) or once <see cref="DefaultMaxWait"/> has elapsed since the oldest pending path
/// (whichever comes first), so a continuously busy tree cannot starve the consumer.
/// </para>
/// <para>
/// Merge rules (locked by tests):
/// <list type="bullet">
///   <item><description>Created + Changed on the same path collapse into a single "re-read final state" path.</description></item>
///   <item><description>Created/Changed followed by Deleted becomes a removal (final state wins).</description></item>
///   <item><description>Deleted followed by Created becomes a re-read (it is <b>not</b> a removal).</description></item>
///   <item><description>Renamed always yields both "old path to remove" and "new path to re-read".</description></item>
///   <item><description>Re-read paths and removal paths are deduplicated independently.</description></item>
/// </list>
/// </para>
/// <para>No IO, no indexing, no fingerprinting. Time is injectable so tests never sleep on real windows.</para>
/// </summary>
public sealed class CodeIndexChangeCoalescer
{
    /// <summary>Maximum number of distinct pending paths before fine-grained work is folded into a scope reconcile.</summary>
    public const int DefaultMaxPendingPaths = 20000;

    /// <summary>Quiet period after the last observation before a batch is produced.</summary>
    public static readonly TimeSpan DefaultSilenceWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on how long the oldest pending path may wait, even if changes keep arriving.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly CodeIndexChangeQueue _queue;
    private readonly CodeIndexScopeState _state;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _silenceWindow;
    private readonly TimeSpan _maxWait;
    private readonly int _maxPendingPaths;
    private readonly ILogger? _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _pathsToReindex;
    private readonly Dictionary<string, long> _pathsToRemove;
    private DateTimeOffset? _oldestPendingAtUtc;
    private DateTimeOffset? _lastObservedAtUtc;
    private long _maxObservedSequence;
    private bool _pendingReconcileNotice;
    private long _collapseCount;

    /// <summary>Creates a coalescer.</summary>
    /// <param name="queue">Source queue the coalescer drains.</param>
    /// <param name="state">Scope state (path-budget collapse flags the scope; the batch reports its reconcile flag).</param>
    /// <param name="timeProvider">Optional clock, injectable for deterministic tests.</param>
    /// <param name="silenceWindow">Optional silence window override (default <see cref="DefaultSilenceWindow"/>).</param>
    /// <param name="maxWait">Optional maximum wait override (default <see cref="DefaultMaxWait"/>).</param>
    /// <param name="maxPendingPaths">Optional pending-path budget override (default <see cref="DefaultMaxPendingPaths"/>).</param>
    /// <param name="logger">Optional logger; only the path-budget collapse logs.</param>
    public CodeIndexChangeCoalescer(
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state,
        TimeProvider? timeProvider = null,
        TimeSpan? silenceWindow = null,
        TimeSpan? maxWait = null,
        int maxPendingPaths = DefaultMaxPendingPaths,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(state);

        _silenceWindow = silenceWindow ?? DefaultSilenceWindow;
        _maxWait = maxWait ?? DefaultMaxWait;
        _maxPendingPaths = maxPendingPaths;

        if (_silenceWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(silenceWindow), _silenceWindow, "Silence window must be positive.");
        if (_maxWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxWait), _maxWait, "Max wait must be positive.");
        if (_maxPendingPaths <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPaths), _maxPendingPaths, "Pending path budget must be positive.");

        _queue = queue;
        _state = state;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _pathsToReindex = new Dictionary<string, long>(CodePathIdentity.PathComparer);
        _pathsToRemove = new Dictionary<string, long>(CodePathIdentity.PathComparer);
    }

    /// <summary>Effective silence window.</summary>
    public TimeSpan SilenceWindow => _silenceWindow;

    /// <summary>Effective maximum wait.</summary>
    public TimeSpan MaxWait => _maxWait;

    /// <summary>Effective pending-path budget.</summary>
    public int MaxPendingPaths => _maxPendingPaths;

    /// <summary>Number of distinct paths currently pending across both lists.</summary>
    public int PendingPathCount
    {
        get { lock (_gate) { return _pathsToReindex.Count + _pathsToRemove.Count; } }
    }

    /// <summary>Number of times the pending-path budget was exceeded and fine-grained work was collapsed.</summary>
    public long CollapseCount
    {
        get { lock (_gate) { return _collapseCount; } }
    }

    /// <summary>Drains every observation currently buffered in the queue into the pending set.</summary>
    /// <returns>Number of observations consumed.</returns>
    public int Accumulate()
    {
        var consumed = 0;

        while (_queue.Reader.TryRead(out var change))
        {
            Accept(change);
            consumed++;
        }

        return consumed;
    }

    /// <summary>Merges a single observation into the pending sets.</summary>
    public void Accept(IndexChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            // Debounce is driven by when the change was *observed*, not by when it was ingested:
            // an observation can sit in the queue while the consumer is busy, and using ingestion
            // time would restart the silence window for something that is already old.
            var observedAtUtc = change.ObservedAtUtc;

            Apply(change);

            _lastObservedAtUtc = MaxTimestamp(_lastObservedAtUtc, observedAtUtc);
            _oldestPendingAtUtc = MinTimestamp(_oldestPendingAtUtc, observedAtUtc);
            _maxObservedSequence = Math.Max(_maxObservedSequence, change.Sequence);

            EnforcePathBudget(observedAtUtc);
        }
    }

    /// <summary>
    /// Drains the queue and, when the debounce window has elapsed, produces a batch.
    /// Non-blocking; returns <c>false</c> while the window is still open.
    /// </summary>
    public bool TryDrain(out CodeIndexChangeBatch? batch) => TryDrainAt(_timeProvider.GetUtcNow(), out batch);

    /// <summary>
    /// Deterministic seam: same as <see cref="TryDrain(out CodeIndexChangeBatch?)"/> but with an explicit
    /// clock reading, so tests never wait on real time.
    /// </summary>
    internal bool TryDrainAt(DateTimeOffset nowUtc, out CodeIndexChangeBatch? batch)
    {
        batch = null;

        Accumulate();

        CodeIndexChangeBatch? produced;
        lock (_gate)
        {
            produced = BuildBatchIfDue(nowUtc);
        }

        if (produced is null)
            return false;

        batch = produced;
        return true;
    }

    /// <summary>
    /// Waits until a batch can be produced. Returns <c>null</c> when the queue is completed and no work
    /// remains.
    /// </summary>
    public async ValueTask<CodeIndexChangeBatch?> WaitForBatchAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryDrain(out var batch))
                return batch;

            TimeSpan? delay;
            lock (_gate)
            {
                delay = ComputeNextDeadline(_timeProvider.GetUtcNow());
            }

            if (delay is null)
            {
                bool hasMore;
                try
                {
                    hasMore = await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }

                if (!hasMore)
                    return null;

                continue;
            }

            var wait = delay.Value;
            if (wait < MinimumWait)
                wait = MinimumWait;

            await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Apply(IndexChange change)
    {
        switch (change.Kind)
        {
            case IndexChangeKind.Deleted:
                MarkRemove(change.FullPath, change.Sequence);
                break;

            case IndexChangeKind.Renamed:
                if (!string.IsNullOrEmpty(change.OldFullPath))
                    MarkRemove(change.OldFullPath, change.Sequence);

                MarkReindex(change.FullPath, change.Sequence);
                break;

            default:
                // Created / Changed: the path exists, so the final state is "re-read it".
                MarkReindex(change.FullPath, change.Sequence);
                break;
        }
    }

    private void MarkReindex(string path, long sequence)
    {
        if (path.Length == 0)
            return;

        _pathsToRemove.Remove(path);
        _pathsToReindex[path] = _pathsToReindex.TryGetValue(path, out var existing)
            ? Math.Max(existing, sequence)
            : sequence;
    }

    private void MarkRemove(string path, long sequence)
    {
        if (path.Length == 0)
            return;

        _pathsToReindex.Remove(path);
        _pathsToRemove[path] = _pathsToRemove.TryGetValue(path, out var existing)
            ? Math.Max(existing, sequence)
            : sequence;
    }

    /// <summary>
    /// Folds fine-grained work into a scope-level reconcile once the pending-path budget is exceeded:
    /// the path records are released (memory safety) and the scope is flagged.
    /// </summary>
    private void EnforcePathBudget(DateTimeOffset observedAtUtc)
    {
        if (_pathsToReindex.Count + _pathsToRemove.Count <= _maxPendingPaths)
            return;

        _collapseCount++;
        _pathsToReindex.Clear();
        _pathsToRemove.Clear();
        _oldestPendingAtUtc = observedAtUtc;
        _pendingReconcileNotice = true;
        _state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.PathLimitExceeded);

        try
        {
            _logger?.LogWarning(
                "[CodeIndexChangeCoalescer] pending path budget {MaxPendingPaths} exceeded for scope {ScopeId}; fine-grained work collapsed into a scope reconcile (#{CollapseCount}).",
                _maxPendingPaths,
                _state.ScopeId,
                _collapseCount);
        }
        catch
        {
            // best effort — logging must never break capture
        }
    }

    /// <summary>
    /// Produces a batch when due (must be called under <see cref="_gate"/>).
    /// <para>
    /// A batch without paths is produced only to announce the coalescer's own path-budget collapse.
    /// A reconcile flag coming from elsewhere (e.g. a queue overflow recorded by the watcher) is
    /// reported on the next batch that does have paths, and is also readable directly from
    /// <see cref="CodeIndexScopeState"/> — it must not produce empty batches forever.
    /// </para>
    /// </summary>
    private CodeIndexChangeBatch? BuildBatchIfDue(DateTimeOffset nowUtc)
    {
        var hasPendingPaths = _pathsToReindex.Count + _pathsToRemove.Count > 0;
        var reconcileNotice = _pendingReconcileNotice;

        if (!hasPendingPaths && !reconcileNotice)
            return null;

        if (hasPendingPaths)
        {
            var silenceElapsed = _lastObservedAtUtc is { } lastObserved
                && nowUtc - lastObserved >= _silenceWindow;
            var maxWaitElapsed = _oldestPendingAtUtc is { } oldest
                && nowUtc - oldest >= _maxWait;

            if (!silenceElapsed && !maxWaitElapsed)
                return null;
        }

        var pathsToReindex = hasPendingPaths
            ? new List<string>(_pathsToReindex.Keys)
            : new List<string>();
        var pathsToRemove = hasPendingPaths
            ? new List<string>(_pathsToRemove.Keys)
            : new List<string>();

        // Everything observed since the previous batch is covered by this batch: as paths, or by the
        // reconcile that replaced them. So the version is simply the highest sequence seen so far.
        var version = _maxObservedSequence;

        _pathsToReindex.Clear();
        _pathsToRemove.Clear();
        _oldestPendingAtUtc = null;
        _lastObservedAtUtc = null;
        _maxObservedSequence = 0;
        _pendingReconcileNotice = false;

        var reconcileRequired = _state.NeedsReconcile;

        return new CodeIndexChangeBatch(
            _state.WorkspaceId,
            _state.ScopeId,
            version,
            pathsToReindex,
            pathsToRemove,
            reconcileRequired,
            reconcileRequired ? _state.ReconcileReason : null,
            nowUtc);
    }

    /// <summary>Time until the next batch becomes due, or null when there is nothing pending (call under <see cref="_gate"/>).</summary>
    private TimeSpan? ComputeNextDeadline(DateTimeOffset nowUtc)
    {
        if (_pathsToReindex.Count + _pathsToRemove.Count == 0)
            return null;

        var deadline = TimeSpan.MaxValue;

        if (_lastObservedAtUtc is { } lastObserved)
            deadline = Min(deadline, _silenceWindow - (nowUtc - lastObserved));

        if (_oldestPendingAtUtc is { } oldest)
            deadline = Min(deadline, _maxWait - (nowUtc - oldest));

        return deadline == TimeSpan.MaxValue ? TimeSpan.Zero : deadline;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static DateTimeOffset MaxTimestamp(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } existing && existing >= candidate ? existing : candidate;

    private static DateTimeOffset MinTimestamp(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } existing && existing <= candidate ? existing : candidate;
}
