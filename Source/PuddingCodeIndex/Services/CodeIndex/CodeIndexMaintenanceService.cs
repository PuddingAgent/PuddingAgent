using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Connects the change-capture pipeline (U3-A) to the index scheduler: every coalesced
/// <see cref="CodeIndexChangeBatch"/> produces a real indexing run, and a request that arrives while a
/// scope is being indexed is never dropped.
/// <para>
/// One change-capture pipeline is owned per scope (change source, bounded queue, scope state,
/// coalescer). A batch is handled at <b>scope</b> granularity — per-file incremental commit is U3-B3 —
/// but the batch payload is never silently ignored: removal paths are counted/exposed and a reconcile
/// request becomes visible scope state plus a counter and a log line.
/// </para>
/// <para>
/// The driver owns the single processing loop; <see cref="CodeIndexScheduler"/> deliberately owns none.
/// Hosting (DI + <c>IHostedService</c>) is U3-B2, so this component never depends on the Host.
/// </para>
/// </summary>
public sealed class CodeIndexMaintenanceService : ICodeIndexMaintenance, IDisposable
{
    /// <summary>Default cadence at which the driver re-checks for due batches.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Default upper bound on how long <see cref="StopAsync"/> waits for an in-flight batch.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Per-scope pipeline plus the counters this driver keeps about it.</summary>
    private sealed class ScopeEntry
    {
        public ScopeEntry(
            string rootPath,
            CodeIndexChangeQueue queue,
            CodeIndexScopeState state,
            CodeIndexChangeCoalescer coalescer)
        {
            RootPath = rootPath;
            Queue = queue;
            State = state;
            Coalescer = coalescer;
        }

        public string RootPath { get; }

        public CodeIndexChangeQueue Queue { get; }

        public CodeIndexScopeState State { get; }

        public CodeIndexChangeCoalescer Coalescer { get; }

        public ICodeIndexChangeWatcher? Watcher { get; set; }

        /// <summary>Guarded by the service gate.</summary>
        public long ReconcileRequestCount;

        /// <summary>Guarded by the service gate.</summary>
        public long RemovalObservationCount;

        /// <summary>Guarded by the service gate.</summary>
        public string[] LastRemovalPaths = [];
    }

    private readonly ICodeIndexSchedulerDriver _scheduler;
    private readonly ICodeIndexWatcherFactory _watcherFactory;
    private readonly ILogger<CodeIndexMaintenanceService>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _queueCapacity;
    private readonly TimeSpan _silenceWindow;
    private readonly TimeSpan _maxWait;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _stopTimeout;

    private readonly Dictionary<(string WorkspaceId, string ScopeId), ScopeEntry> _scopes = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _running;
    private long _batchesProcessed;
    private long _reconcileRequests;
    private long _removalObservations;
    private long _abandonedBatches;
    private long _rejectedScopeRequests;

    /// <summary>Creates the driver. Nothing runs until <see cref="StartAsync"/>.</summary>
    /// <param name="scheduler">Index scheduler port (the driver pumps it explicitly).</param>
    /// <param name="watcherFactory">Creates the change source of each attached scope.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">Optional clock, injectable for deterministic tests.</param>
    /// <param name="queueCapacity">Bounded queue capacity per scope.</param>
    /// <param name="silenceWindow">Debounce silence window override.</param>
    /// <param name="maxWait">Debounce maximum-wait override.</param>
    /// <param name="pollInterval">Driver poll cadence (injectable so callers can drive explicitly).</param>
    /// <param name="stopTimeout">Upper bound on how long <see cref="StopAsync"/> waits for an in-flight batch.</param>
    public CodeIndexMaintenanceService(
        ICodeIndexSchedulerDriver scheduler,
        ICodeIndexWatcherFactory watcherFactory,
        ILogger<CodeIndexMaintenanceService>? logger = null,
        TimeProvider? timeProvider = null,
        int queueCapacity = CodeIndexChangeQueue.DefaultCapacity,
        TimeSpan? silenceWindow = null,
        TimeSpan? maxWait = null,
        TimeSpan? pollInterval = null,
        TimeSpan? stopTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(watcherFactory);

        _pollInterval = pollInterval ?? DefaultPollInterval;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;

        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), _pollInterval, "Poll interval must be positive.");
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), _stopTimeout, "Stop timeout must be positive.");

        _scheduler = scheduler;
        _watcherFactory = watcherFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queueCapacity = queueCapacity;
        _silenceWindow = silenceWindow ?? CodeIndexChangeCoalescer.DefaultSilenceWindow;
        _maxWait = maxWait ?? CodeIndexChangeCoalescer.DefaultMaxWait;
    }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <inheritdoc />
    public long BatchesProcessed => Interlocked.Read(ref _batchesProcessed);

    /// <inheritdoc />
    public long ReconcileRequests => Interlocked.Read(ref _reconcileRequests);

    /// <inheritdoc />
    public long RemovalObservations => Interlocked.Read(ref _removalObservations);

    /// <summary>Number of stops that had to return while a batch was still in flight (bounded stop).</summary>
    public long AbandonedBatches => Interlocked.Read(ref _abandonedBatches);

    /// <summary>Number of scope attachments refused because the driver was not running.</summary>
    public long RejectedScopeRequests => Interlocked.Read(ref _rejectedScopeRequests);

    /// <inheritdoc />
    public int PendingReconcileScopeCount
    {
        get
        {
            var count = 0;
            foreach (var (_, entry) in SnapshotScopeEntries())
            {
                if (entry.State.NeedsReconcile)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Attaches a scope and starts its change source.
    /// <para>
    /// Only accepted while the driver is running — that is what makes "no new change is accepted after
    /// <see cref="StopAsync"/>" observable rather than aspirational.
    /// </para>
    /// </summary>
    /// <param name="workspaceId">Workspace that owns the scope.</param>
    /// <param name="scopeId">Scope to attach.</param>
    /// <param name="rootPath">Root directory the scope is watched at.</param>
    /// <returns><c>true</c> when the scope was attached; <c>false</c> when it already was or the driver is stopped.</returns>
    public bool EnsureScope(string workspaceId, string scopeId, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!IsRunning)
        {
            Interlocked.Increment(ref _rejectedScopeRequests);
            _logger?.LogWarning(
                "[CodeIndexMaintenance] Refusing scope {ScopeId}: the driver is not running.", scopeId);
            return false;
        }

        lock (_gate)
        {
            var key = (workspaceId, scopeId);
            if (_scopes.ContainsKey(key))
                return false;

            var state = new CodeIndexScopeState(workspaceId, scopeId);
            var queue = new CodeIndexChangeQueue(_queueCapacity);
            var coalescer = new CodeIndexChangeCoalescer(
                queue, state, _timeProvider, _silenceWindow, _maxWait, logger: _logger);

            var entry = new ScopeEntry(rootPath, queue, state, coalescer);

            ICodeIndexChangeWatcher? watcher = null;
            try
            {
                watcher = _watcherFactory.Create(workspaceId, scopeId, rootPath, queue, state);
            }
            catch (Exception ex)
            {
                // A failing change source must not take the driver down, but it must be visible: the
                // status reports WatcherAttached=false instead of pretending the scope is being watched.
                _logger?.LogWarning(ex,
                    "[CodeIndexMaintenance] Change source for scope {ScopeId} could not be created.", scopeId);
            }

            entry.Watcher = watcher;
            _scopes[key] = entry;
            watcher?.Start();
        }

        _logger?.LogInformation(
            "[CodeIndexMaintenance] Attached scope {ScopeId} in {WorkspaceId} at {RootPath}.",
            scopeId, workspaceId, rootPath);
        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _loopCts = new CancellationTokenSource();
            Volatile.Write(ref _running, 1);
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }

        _logger?.LogInformation(
            "[CodeIndexMaintenance] Driver started (poll interval {PollInterval}).", _pollInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? loopCts;
        Task? loopTask;

        lock (_gate)
        {
            if (!IsRunning)
                return;

            Volatile.Write(ref _running, 0);
            loopCts = _loopCts;
            loopTask = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        // (1) Detach every change source: after this point no new observation can be published for an
        //     attached scope. Observations already buffered are deliberately left in place (and the scope
        //     stays dirty) so nothing is discarded by the shutdown itself.
        foreach (var (_, entry) in SnapshotScopeEntries())
        {
            var watcher = entry.Watcher;
            entry.Watcher = null;

            try
            {
                watcher?.Stop();
            }
            catch
            {
                // best effort: shutdown must not fail because a change source refused to stop
            }

            try
            {
                watcher?.Dispose();
            }
            catch
            {
                // best effort
            }
        }

        // (2) Cancel the loop and wait for an in-flight batch, but never past the bounded timeout.
        try
        {
            loopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }

        if (loopTask is not null)
        {
            var completedInTime = false;
            try
            {
                var timeout = Task.Delay(_stopTimeout, _timeProvider, cancellationToken);
                completedInTime = ReferenceEquals(await Task.WhenAny(loopTask, timeout).ConfigureAwait(false), loopTask);
            }
            catch (OperationCanceledException)
            {
                completedInTime = false;
            }

            if (!completedInTime)
            {
                Interlocked.Increment(ref _abandonedBatches);
                _logger?.LogWarning(
                    "[CodeIndexMaintenance] A batch was still in flight after {StopTimeout}; stop returns anyway (the run is cancelled and its job stays queued).",
                    _stopTimeout);
            }
            else if (loopTask.IsFaulted)
            {
                _logger?.LogWarning(loopTask.Exception, "[CodeIndexMaintenance] Driver loop ended with a fault.");
            }
        }

        loopCts?.Dispose();
    }

    /// <summary>
    /// Deterministic pump step: handles every attached scope whose debounce window has elapsed.
    /// <para>
    /// This is the same operation the driver loop performs, exposed so a caller (or a test) can drive the
    /// pipeline without depending on wall-clock polling.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the pump.</param>
    /// <returns>Number of batches handled.</returns>
    public async Task<int> ProcessDueBatchesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            return 0;

        var handled = 0;

        foreach (var (_, entry) in SnapshotScopeEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.Coalescer.TryDrain(out var batch) || batch is null)
                continue;

            await HandleBatchAsync(entry, batch, cancellationToken).ConfigureAwait(false);
            handled++;
        }

        return handled;
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeIndexMaintenanceScopeStatus> GetScopeStatuses() =>
        SnapshotScopeEntries().Select(pair => BuildStatus(pair.Key.WorkspaceId, pair.Key.ScopeId, pair.Value)).ToArray();

    /// <inheritdoc />
    public CodeIndexMaintenanceScopeStatus? GetScopeStatus(string workspaceId, string scopeId)
    {
        ScopeEntry? entry;
        lock (_gate)
        {
            _scopes.TryGetValue((workspaceId, scopeId), out entry);
        }

        return entry is null ? null : BuildStatus(workspaceId, scopeId, entry);
    }

    /// <summary>Turns one coalesced batch into a real indexing run through the scheduler port.</summary>
    private async Task HandleBatchAsync(
        ScopeEntry entry,
        CodeIndexChangeBatch batch,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _batchesProcessed);

        // Removal paths are exposed and counted. This slice does NOT delete anything per file (that is
        // U3-B3), and it must not pretend it did.
        if (batch.PathsToRemove.Count > 0)
        {
            lock (_gate)
            {
                entry.RemovalObservationCount += batch.PathsToRemove.Count;
                entry.LastRemovalPaths = batch.PathsToRemove.ToArray();
            }

            Interlocked.Add(ref _removalObservations, batch.PathsToRemove.Count);

            _logger?.LogInformation(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version}: {RemovalCount} removal path(s) observed (exposed only — per-file removal is U3-B3).",
                batch.ScopeId, batch.Version, batch.PathsToRemove.Count);
        }

        // A reconcile request becomes visible state (CodeIndexScopeState.NeedsReconcile), a counter and a
        // log line, and it stays visible until the calibration slice (U3-C) clears it.
        if (batch.ReconcileRequired)
        {
            lock (_gate)
            {
                entry.ReconcileRequestCount++;
            }

            Interlocked.Increment(ref _reconcileRequests);

            _logger?.LogWarning(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version} requires reconciliation ({Reason}); the scope stays flagged until calibration clears it.",
                batch.ScopeId, batch.Version, batch.ReconcileReason ?? "unspecified");
        }

        // Scope-granular re-index through the existing scheduler port. Riding the scheduler also means a
        // change that lands while this run is in flight is re-queued instead of dropped.
        _scheduler.Enqueue(batch.WorkspaceId, batch.ScopeId);

        var completed = await _scheduler.ProcessPendingAsync(cancellationToken).ConfigureAwait(false);

        if (completed == 0)
        {
            _logger?.LogWarning(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version} produced no completed indexing run (scope missing/removed, or the run was cancelled).",
                batch.ScopeId, batch.Version);
        }
    }

    /// <summary>Driver loop: re-checks for due batches on the configured cadence.</summary>
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                await ProcessDueBatchesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single failing batch must not kill the driver.
                _logger?.LogError(ex, "[CodeIndexMaintenance] Driver loop iteration failed; continuing.");
            }
        }
    }

    private CodeIndexMaintenanceScopeStatus BuildStatus(string workspaceId, string scopeId, ScopeEntry entry)
    {
        var progress = _scheduler.GetProgress(workspaceId, scopeId);
        var state = entry.State;

        long reconcileRequestCount;
        long removalObservationCount;
        string[] lastRemovalPaths;
        lock (_gate)
        {
            reconcileRequestCount = entry.ReconcileRequestCount;
            removalObservationCount = entry.RemovalObservationCount;
            lastRemovalPaths = entry.LastRemovalPaths;
        }

        return new CodeIndexMaintenanceScopeStatus(
            workspaceId,
            scopeId,
            entry.RootPath,
            state.ObservedVersion,
            progress.DesiredVersion,
            progress.CommittedVersion,
            progress.MarkedWhileInFlightCount,
            progress.Pending,
            progress.InFlight,
            state.NeedsReconcile,
            state.ReconcileReason,
            reconcileRequestCount,
            removalObservationCount,
            lastRemovalPaths,
            entry.Watcher is not null);
    }

    private KeyValuePair<(string WorkspaceId, string ScopeId), ScopeEntry>[] SnapshotScopeEntries()
    {
        lock (_gate)
        {
            return _scopes.ToArray();
        }
    }

    /// <summary>Stops the driver (bounded) and detaches every change source. Idempotent; never throws.</summary>
    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CodeIndexMaintenance] Dispose could not stop the driver cleanly.");
        }

        foreach (var (_, entry) in SnapshotScopeEntries())
        {
            var watcher = entry.Watcher;
            entry.Watcher = null;

            try
            {
                watcher?.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }
}
