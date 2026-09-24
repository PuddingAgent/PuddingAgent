using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Connects the change-capture pipeline (U3-A) to the index: every coalesced
/// <see cref="CodeIndexChangeBatch"/> produces real index work, and a request that arrives while a scope is
/// being indexed is never dropped.
/// <para>
/// One change-capture pipeline is owned per scope (change source, bounded queue, scope state,
/// coalescer). A batch is applied <b>per file</b> (U3-B3): removal paths are deleted from the store
/// (file record + symbols + the graph rows they own, one transaction per batch, idempotent), and changed
/// paths are re-indexed one file at a time through <see cref="ICodeIndexFileUpdater.IndexFileAsync"/> when the
/// registered indexer implements that capability. Only what
/// cannot be handled per file — a reconcile request, a directory change, or an indexer that refuses a path
/// — escalates to the scope-level run, so a batch payload is never silently ignored.
/// </para>
    /// <para>
    /// <b>U3-C calibration</b>: a scope whose fine-grained capture can no longer be trusted is reconciled by a
    /// calibration sweep — every indexed path of the scope is checked against the file system and the ones that
    /// are gone are removed from the index — instead of by a scope-level re-index alone. A scope-level run
    /// re-reads the files that exist; it has no notion of "seen this run", so it can never clear the rows of
    /// files that are gone. A successful sweep clears <c>NeedsReconcile</c>; a refused or truncated one keeps it.
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

    /// <summary>
    /// Minimum interval between two calibration attempts for the same scope.
    /// <para>
    /// Reconcile is an error path, and a root that stays unavailable must not be re-probed (and re-logged) at the
    /// driver's poll cadence, so an attempt no longer than once a minute is a deliberate bound. A scope that is
    /// flagged for the first time is calibrated on the very next step — this interval throttles retries, it does
    /// not delay the first attempt. (Cadence reference: ADR-089 §U3-C "监听不可用时 60s 轮询".)
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultCalibrationInterval = TimeSpan.FromSeconds(60);

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

        /// <summary>Guarded by the service gate. Files whose records were really deleted from the store.</summary>
        public long RemovedFileCount;

        /// <summary>Guarded by the service gate. Files re-indexed one by one instead of by a full run.</summary>
        public long IncrementallyIndexedFileCount;

        /// <summary>Guarded by the service gate. Batches that had to escalate to a scope-level run.</summary>
        public long ScopeEscalationCount;

        /// <summary>
        /// Guarded by the service gate. When the change pipeline last observed this path (fed by every handled
        /// batch). Calibration leaves exactly these paths alone inside its grace window: the pipeline owns them
        /// for now, so sweeping them would race it.
        /// </summary>
        public readonly Dictionary<string, DateTimeOffset> RecentObservations = new(CodePathIdentity.PathComparer);

        /// <summary>Guarded by the service gate. Calibration runs completed (successful and refused alike).</summary>
        public long CalibrationRunCount;

        /// <summary>Guarded by the service gate. Calibration runs refused because the scope root was unusable.</summary>
        public long RejectedCalibrationRunCount;

        /// <summary>Guarded by the service gate. Stale indexed files calibration really removed.</summary>
        public long SweptFileCount;

        /// <summary>Guarded by the service gate. Start of the latest calibration attempt (throttles retries).</summary>
        public DateTimeOffset? LastCalibrationAttemptAtUtc;

        /// <summary>Guarded by the service gate. End of the latest calibration run.</summary>
        public DateTimeOffset? LastCalibrationAtUtc;
    }

    private readonly ICodeIndexSchedulerDriver _scheduler;
    private readonly ICodeIndexWatcherFactory _watcherFactory;
    private readonly ICodeIndexStore _store;
    private readonly ICodeIndexer _indexer;
    private readonly ICodeIndexFileUpdater? _fileUpdater;
    private readonly CodeIndexCalibrationService _calibration;
    private readonly ICodeWorkspaceResolver _resolver;
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
    /// <param name="store">Store a batch's per-file removals are applied through.</param>
    /// <param name="indexer">Per-file indexer used for the changed paths of a batch.</param>
    /// <param name="resolver">Resolves a scope id into the workspace descriptor a per-file run needs.</param>
    /// <param name="calibration">
    /// Calibration used to sweep the stale rows of a flagged scope (U3-C). When omitted, one is built over the
    /// same store and clock.
    /// </param>
    public CodeIndexMaintenanceService(
        ICodeIndexSchedulerDriver scheduler,
        ICodeIndexWatcherFactory watcherFactory,
        ICodeIndexStore store,
        ICodeIndexer indexer,
        ICodeWorkspaceResolver resolver,
        ILogger<CodeIndexMaintenanceService>? logger = null,
        TimeProvider? timeProvider = null,
        int queueCapacity = CodeIndexChangeQueue.DefaultCapacity,
        TimeSpan? silenceWindow = null,
        TimeSpan? maxWait = null,
        TimeSpan? pollInterval = null,
        TimeSpan? stopTimeout = null,
        CodeIndexCalibrationService? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(watcherFactory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(resolver);

        _pollInterval = pollInterval ?? DefaultPollInterval;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;

        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), _pollInterval, "Poll interval must be positive.");
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), _stopTimeout, "Stop timeout must be positive.");

        _scheduler = scheduler;
        _watcherFactory = watcherFactory;
        _store = store;
        _indexer = indexer;
        _fileUpdater = indexer as ICodeIndexFileUpdater;
        _resolver = resolver;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queueCapacity = queueCapacity;
        _silenceWindow = silenceWindow ?? CodeIndexChangeCoalescer.DefaultSilenceWindow;
        _maxWait = maxWait ?? CodeIndexChangeCoalescer.DefaultMaxWait;
        _calibration = calibration ?? new CodeIndexCalibrationService(store, _timeProvider, logger: logger);
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

                // U3-C: while the attachment stays broken, fine-grained capture cannot be trusted for this scope
                // — which is exactly what NeedsReconcile means. Flagging it here is what lets the calibration
                // sweep clear the rows a missing watcher can never report.
                state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
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
    /// <para>
    /// The step is always <b>complete</b>: it drains due batches <i>and</i> pumps the scheduler queue. The
    /// queue is fed by every producer, not only by the change pipeline — <c>code_index_register_project</c>
    /// enqueues a project directly, with no change batch behind it. Pumping only inside
    /// <c>HandleBatchAsync</c> would starve exactly those requests: the scheduler owns no worker of its own
    /// (U3-B1), and a scope that was never attached produces no batch at all.
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

        // Unconditional pump. The production acceptor (CodeProjectManagementTools) calls
        // ICodeIndexScheduler.Enqueue directly, with no change batch behind it, so a step that only pumped
        // inside HandleBatchAsync would leave those requests queued forever — that is the P0 defect.
        await _scheduler.ProcessPendingAsync(cancellationToken).ConfigureAwait(false);

        // U3-C: reconcile by calibration. Deliberately the last thing the step does — the flagged scope's batch
        // has been applied and the queue has been pumped, so the sweep sees the pipeline's own result instead of
        // racing it, and paths the batch just touched are still inside the calibration grace window.
        await CalibrateReconcileScopesAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Handles one coalesced batch and makes a failure visible: a batch that cannot be applied flags the scope
    /// as needing reconciliation instead of disappearing silently.
    /// </summary>
    private async Task HandleBatchAsync(
        ScopeEntry entry,
        CodeIndexChangeBatch batch,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _batchesProcessed);

        try
        {
            await ApplyBatchAsync(entry, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation (shutdown or an explicit pump cancel) must stay observable to the caller.
            throw;
        }
        catch (Exception ex)
        {
            // The batch is lost, so say so: the scope stays flagged for the calibration slice (U3-C), which
            // owns full reconciliation, and the failure is not mistaken for "nothing to do".
            entry.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.BatchApplicationFailed);

            _logger?.LogError(ex,
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version} could not be applied; the scope is flagged for reconciliation.",
                batch.ScopeId, batch.Version);
        }
    }

    /// <summary>
    /// Applies one batch: the removal paths are removed, the changed paths are re-indexed one file at a time,
    /// and only what cannot be handled per file escalates to a scope-level run through the scheduler port.
    /// </summary>
    private async Task ApplyBatchAsync(
        ScopeEntry entry,
        CodeIndexChangeBatch batch,
        CancellationToken cancellationToken)
    {
        // U3-C: from here on the change pipeline owns these paths for a grace window, so the calibration sweep
        // must leave them alone until the pipeline has settled them.
        RecordRecentObservations(entry, batch);

        // Removal paths are really removed (U3-B3): the store deletes the file record, its symbols and the
        // graph rows owned by them in one transaction. A path that is not indexed stays a safe no-op.
        if (batch.PathsToRemove.Count > 0)
        {
            var removed = await _store.RemoveFilesAsync(
                batch.WorkspaceId, batch.ScopeId, batch.PathsToRemove, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                entry.RemovalObservationCount += batch.PathsToRemove.Count;
                entry.LastRemovalPaths = batch.PathsToRemove.ToArray();
                entry.RemovedFileCount += removed;
            }

            Interlocked.Add(ref _removalObservations, batch.PathsToRemove.Count);

            _logger?.LogInformation(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version}: removed {RemovedCount} indexed file(s) of {RemovalCount} observed removal path(s).",
                batch.ScopeId, batch.Version, removed, batch.PathsToRemove.Count);
        }

        // A reconcile request becomes visible state (CodeIndexScopeState.NeedsReconcile), a counter and a
        // log line, and it stays visible until the calibration slice (U3-C) clears it. It also forces a
        // scope-level run, because fine-grained capture can no longer be trusted for this batch.
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

        // Per-file increment: a changed file no longer forces a full scope re-index. Whatever the indexer
        // declines (or a directory change, whose subtree can hold files the batch never mentioned) escalates
        // to a scope-level run instead of being dropped.
        var escalate = batch.ReconcileRequired;
        if (!escalate && batch.PathsToReindex.Count > 0)
            escalate = !await IndexChangedFilesAsync(entry, batch, cancellationToken).ConfigureAwait(false);

        if (escalate)
        {
            lock (_gate)
            {
                entry.ScopeEscalationCount++;
            }

            _logger?.LogInformation(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version}: escalating to a scope-level indexing run.",
                batch.ScopeId, batch.Version);

            _scheduler.Enqueue(batch.WorkspaceId, batch.ScopeId);
        }

        // The pump is unconditional: producers that never produce a batch (code_index_register_project)
        // enqueue directly, so the batch path must not be the only thing that advances the queue.
        var completed = await _scheduler.ProcessPendingAsync(cancellationToken).ConfigureAwait(false);

        if (escalate && completed == 0)
        {
            _logger?.LogWarning(
                "[CodeIndexMaintenance] Scope {ScopeId} batch v{Version} produced no completed indexing run (scope missing/removed, or the run was cancelled).",
                batch.ScopeId, batch.Version);
        }
    }

    /// <summary>
    /// Re-indexes the changed paths of one batch one file at a time.
    /// </summary>
    /// <returns>
    /// <c>true</c> when every path was handled incrementally. <c>false</c> means the batch needs a
    /// scope-level run: a path is a directory (its subtree may hold files the batch never mentioned), the
    /// indexer refused a path, or the workspace descriptor could not be resolved.
    /// </returns>
    private async Task<bool> IndexChangedFilesAsync(
        ScopeEntry entry,
        CodeIndexChangeBatch batch,
        CancellationToken cancellationToken)
    {
        CodeWorkspaceDescriptor? descriptor = null;

        foreach (var filePath in batch.PathsToReindex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(filePath))
            {
                _logger?.LogInformation(
                    "[CodeIndexMaintenance] Scope {ScopeId}: {Path} is a directory; a scope-level run is required.",
                    batch.ScopeId, filePath);
                return false;
            }

            if (!File.Exists(filePath))
            {
                // The path vanished after it was observed: its final state is "removed" — the same rule the
                // coalescer applies — so it is removed here instead of lingering as a stale entry.
                var vanished = await _store.RemoveFilesAsync(
                    batch.WorkspaceId, batch.ScopeId, [filePath], cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    entry.RemovedFileCount += vanished;
                }

                continue;
            }

            if (_fileUpdater is null)
            {
                // The registered indexer has no per-file capability (it only implements ICodeIndexer):
                // escalate instead of pretending the file was indexed.
                _logger?.LogInformation(
                    "[CodeIndexMaintenance] Scope {ScopeId}: the indexer has no per-file capability; a scope-level run is required.",
                    batch.ScopeId);
                return false;
            }

            descriptor ??= await _resolver.ResolveWorkspaceAsync(
                batch.WorkspaceId, batch.ScopeId, cancellationToken).ConfigureAwait(false);

            if (descriptor is null)
            {
                _logger?.LogWarning(
                    "[CodeIndexMaintenance] Scope {ScopeId}: workspace descriptor could not be resolved; falling back to a scope-level run.",
                    batch.ScopeId);
                return false;
            }

            var result = await _fileUpdater.IndexFileAsync(descriptor, filePath, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger?.LogWarning(
                    "[CodeIndexMaintenance] Scope {ScopeId}: per-file indexing of {Path} failed ({Status}: {Message}); falling back to a scope-level run.",
                    batch.ScopeId, filePath, result.Status, result.Message);
                return false;
            }

            lock (_gate)
            {
                entry.IncrementallyIndexedFileCount++;
            }
        }

        return true;
    }

    /// <summary>
    /// Records that the change pipeline observed every path of <paramref name="batch"/> just now. These
    /// timestamps are what keeps calibration out of the pipeline's way: a path observed inside the grace window
    /// is never swept, even when it is momentarily absent on disk.
    /// <para>
    /// Entries that aged out of the window are dropped here, not only when a calibration reads the map: only
    /// paths inside the window are ever consulted, and a scope that is never flagged would otherwise accumulate
    /// one entry per distinct path it ever observed.
    /// </para>
    /// </summary>
    private void RecordRecentObservations(ScopeEntry entry, CodeIndexChangeBatch batch)
    {
        var observedAtUtc = _timeProvider.GetUtcNow();
        var cutoffUtc = observedAtUtc - _calibration.GraceWindow;

        lock (_gate)
        {
            foreach (var path in batch.PathsToReindex)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    entry.RecentObservations[path] = observedAtUtc;
            }

            foreach (var path in batch.PathsToRemove)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    entry.RecentObservations[path] = observedAtUtc;
            }

            List<string>? expired = null;
            foreach (var (path, pathObservedAtUtc) in entry.RecentObservations)
            {
                if (pathObservedAtUtc < cutoffUtc)
                    (expired ??= []).Add(path);
            }

            if (expired is not null)
            {
                foreach (var path in expired)
                    entry.RecentObservations.Remove(path);
            }
        }
    }

    /// <summary>
    /// Paths this scope's change pipeline observed inside the grace window. Entries that aged out are pruned here,
    /// so the observation map cannot grow without bound on a long-running scope.
    /// </summary>
    private Dictionary<string, DateTimeOffset> SnapshotRecentObservations(
        ScopeEntry entry,
        DateTimeOffset nowUtc,
        TimeSpan graceWindow)
    {
        var snapshot = new Dictionary<string, DateTimeOffset>(CodePathIdentity.PathComparer);

        lock (_gate)
        {
            foreach (var (path, observedAtUtc) in entry.RecentObservations.ToArray())
            {
                if (nowUtc - observedAtUtc > graceWindow)
                {
                    entry.RecentObservations.Remove(path);
                    continue;
                }

                snapshot[path] = observedAtUtc;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Reconciles every scope that needs it by <b>calibrating</b> its index, and clears the flag when the sweep
    /// was allowed to run to the end.
    /// <para>
    /// A refused sweep (root missing/unreadable) and a truncated one both keep the scope flagged, so "I could not
    /// tell" is never mistaken for "nothing was stale". Attempts are throttled per scope
    /// (<see cref="DefaultCalibrationInterval"/>), so a root that stays unavailable cannot be re-probed — and
    /// re-logged — at the driver's poll cadence.
    /// </para>
    /// </summary>
    private async Task CalibrateReconcileScopesAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();

        foreach (var (key, entry) in SnapshotScopeEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.State.NeedsReconcile)
                continue;

            lock (_gate)
            {
                if (entry.LastCalibrationAttemptAtUtc is { } lastAttempt
                    && nowUtc - lastAttempt < DefaultCalibrationInterval)
                {
                    continue;
                }

                entry.LastCalibrationAttemptAtUtc = nowUtc;
            }

            CodeIndexCalibrationResult result;

            try
            {
                result = await _calibration.CalibrateAsync(
                    new CodeIndexCalibrationRequest(
                        key.WorkspaceId,
                        key.ScopeId,
                        entry.RootPath,
                        SnapshotRecentObservations(entry, nowUtc, _calibration.GraceWindow)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.CalibrationFailed);

                _logger?.LogError(ex,
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration failed; the scope stays flagged for reconciliation.",
                    key.ScopeId);
                continue;
            }

            lock (_gate)
            {
                entry.CalibrationRunCount++;
                entry.SweptFileCount += result.SweptFileCount;
                entry.LastCalibrationAtUtc = result.CompletedAtUtc;

                if (!result.RootUsable)
                    entry.RejectedCalibrationRunCount++;
            }

            if (!result.RootUsable)
            {
                // The calibrator logged the refusal itself; here the driver records the consequence it owns:
                // the scope must stay flagged, and nothing was removed.
                entry.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.CalibrationRootUnavailable);

                _logger?.LogError(
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration was refused ({Reason}); the scope stays flagged and no file was removed.",
                    key.ScopeId, result.RejectionReason ?? "unspecified");
                continue;
            }

            if (result.Truncated)
            {
                _logger?.LogWarning(
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration stopped at the per-run ceiling after sweeping {Swept} file(s); the scope stays flagged for a later run.",
                    key.ScopeId, result.SweptFileCount);
                continue;
            }

            entry.State.ClearNeedsReconcile();

            _logger?.LogInformation(
                "[CodeIndexMaintenance] Scope {ScopeId}: calibration swept {Swept} stale file(s) ({Absent} of {Scanned} indexed path(s) are not on disk, {Protected} left alone as recently observed); the reconcile flag is cleared.",
                key.ScopeId, result.SweptFileCount, result.AbsentFileCount, result.ScannedFileCount, result.ProtectedFileCount);
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
        long removedFileCount;
        long incrementallyIndexedFileCount;
        long scopeEscalationCount;
        long sweptFileCount;
        long calibrationRunCount;
        long rejectedCalibrationRunCount;
        DateTimeOffset? lastCalibrationAtUtc;
        lock (_gate)
        {
            reconcileRequestCount = entry.ReconcileRequestCount;
            removalObservationCount = entry.RemovalObservationCount;
            lastRemovalPaths = entry.LastRemovalPaths;
            removedFileCount = entry.RemovedFileCount;
            incrementallyIndexedFileCount = entry.IncrementallyIndexedFileCount;
            scopeEscalationCount = entry.ScopeEscalationCount;
            sweptFileCount = entry.SweptFileCount;
            calibrationRunCount = entry.CalibrationRunCount;
            rejectedCalibrationRunCount = entry.RejectedCalibrationRunCount;
            lastCalibrationAtUtc = entry.LastCalibrationAtUtc;
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
            removedFileCount,
            incrementallyIndexedFileCount,
            scopeEscalationCount,
            sweptFileCount,
            calibrationRunCount,
            rejectedCalibrationRunCount,
            lastCalibrationAtUtc,
            entry.RecentObservations.Count,
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
