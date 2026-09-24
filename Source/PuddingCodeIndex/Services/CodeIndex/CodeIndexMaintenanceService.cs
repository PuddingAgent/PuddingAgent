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
    /// <b>U3-D routine calibration</b>: that sweep used to depend on <c>NeedsReconcile</c>, so it only happened in
    /// the moment something went wrong. Every scope is now calibrated once per
    /// <see cref="DefaultCalibrationPeriod"/> as well, per scope and independent of the flag, because a scope
    /// whose change source goes quiet for good has no other way to lose a stale row. The driver's poll cadence is
    /// untouched by that: the due check is per scope, so a step that finds nothing due sweeps nothing.
    /// </para>
    /// <para>
    /// <b>U3-E calibration retry backoff</b>: a scope whose root (or whose calibrator) keeps failing used to be
    /// re-probed — and re-logged — every 60 s for ever, which U3-D made reachable for any scope whose root
    /// disappears. The retry throttling is now an exponential ladder over
    /// <see cref="DefaultCalibrationInterval"/> — 60 s, 2 min, 4 min, 8 min, 16 min — capped at
    /// <see cref="DefaultCalibrationBackoffMax"/> (30 min), and every sweep that got to read the root puts the
    /// ladder back at 60 s. The routine <see cref="DefaultCalibrationPeriod"/> clock is untouched: the ladder
    /// only paces retries of a scope that already failed.
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
    /// Minimum interval between two calibration attempts for the same scope, and the first rung of the U3-E retry
    /// ladder.
    /// <para>
    /// Reconcile is an error path, and a root that stays unavailable must not be re-probed (and re-logged) at the
    /// driver's poll cadence, so an attempt no longer than once a minute is a deliberate bound. A scope that is
    /// flagged for the first time is calibrated on the very next step — this interval throttles retries, it does
    /// not delay the first attempt. (Cadence reference: ADR-089 §U3-C "监听不可用时 60s 轮询".)
    /// </para>
    /// <para>
    /// U3-E makes it the <b>base</b> of the backoff ladder rather than the whole bound: a scope that keeps being
    /// refused waits this long before its second attempt, twice this long before its third, and so on up to
    /// <see cref="DefaultCalibrationBackoffMax"/>. One failure therefore changes nothing, and only <b>repeated</b>
    /// failure widens the gap.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultCalibrationInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Upper bound of the calibration retry ladder (U3-E): the longest a scope that keeps failing is left alone
    /// before its next attempt.
    /// <para>
    /// The ladder is <see cref="DefaultCalibrationInterval"/> doubled once per consecutive unusable-root outcome —
    /// 60 s, 2 min, 4 min, 8 min, 16 min — and this cap is where it stops (32 min would be the next rung, so the
    /// effective ladder ends one doubling short of it). A scope whose root never comes back is therefore probed
    /// about 52 times on its first day and about 340 times in its first week, instead of the 1440 / 10080 probes
    /// (and error lines) a fixed 60 s retry costs; and a root that <b>does</b> come back is still noticed within
    /// this window, because the ladder never grows past it.
    /// </para>
    /// <para>
    /// Deliberately a component constant rather than a configuration-file setting, exactly like
    /// <see cref="DefaultPollInterval"/> and <see cref="DefaultCalibrationPeriod"/>: the component takes its knobs
    /// as constructor arguments, so this slice changes nothing on the host side.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultCalibrationBackoffMax = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Interval at which every attached scope is calibrated <b>routinely</b> (U3-D), whether or not anything
    /// flagged it for reconciliation.
    /// <para>
    /// Why it exists: calibration used to be reachable only through <c>NeedsReconcile</c>, i.e. only in the
    /// moment a change source failed. A scope whose capture goes quiet for good — a change source that could not
    /// be attached and was never retried, changes made while the process was not running, events lost before
    /// anything flagged the scope — therefore kept every stale row it had, for ever: no change event is left to
    /// mention those paths, and a scope-level re-index re-reads the files that exist, so it can never clear the
    /// rows of the ones that are gone. A routine sweep is the only thing that can.
    /// </para>
    /// <para>
    /// Measured per scope from the <b>completion</b> of that scope's previous calibration run (its attach
    /// instant when none ever ran) — the clock ADR-089 §U3-C fixes for the routine cadence: "正常 metadata
    /// 校准每 15min … 均按上次完成后计时，不并发叠加".
    /// </para>
    /// <para>
    /// Deliberately a component constant rather than a configuration-file setting, like the poll cadence: the
    /// component takes its knobs as constructor arguments, so this slice changes nothing on the host side.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultCalibrationPeriod = TimeSpan.FromMinutes(15);

    /// <summary>Per-scope pipeline plus the counters this driver keeps about it.</summary>
    private sealed class ScopeEntry
    {
        public ScopeEntry(
            string rootPath,
            CodeIndexChangeQueue queue,
            CodeIndexScopeState state,
            CodeIndexChangeCoalescer coalescer,
            DateTimeOffset attachedAtUtc)
        {
            RootPath = rootPath;
            Queue = queue;
            State = state;
            Coalescer = coalescer;
            AttachedAtUtc = attachedAtUtc;
        }

        public string RootPath { get; }

        /// <summary>
        /// The instant this scope was attached to the driver. It is the anchor the routine calibration clock
        /// (U3-D) starts from, because a scope that was never calibrated has no completion time to measure from.
        /// </summary>
        public DateTimeOffset AttachedAtUtc { get; }

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

        /// <summary>
        /// Guarded by the service gate. Consecutive calibration outcomes that showed the scope root was unusable
        /// (a refusal, or a run that threw) — the rung of the U3-E retry ladder this scope currently sits on.
        /// <para>
        /// Every outcome that <b>got to read the root</b> — a finished sweep and a truncated one alike — puts it
        /// back to 0, so the ladder only ever measures <b>repeated</b> failure, and a root that has come back is
        /// not throttled by the history of the outage before it.
        /// </para>
        /// </summary>
        public long ConsecutiveCalibrationFailures;
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

            // The routine calibration clock of this scope (U3-D) is anchored here while it has never run.
            var entry = new ScopeEntry(rootPath, queue, state, coalescer, _timeProvider.GetUtcNow());

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

        // U3-C / U3-D: calibrate by mark-and-sweep. Deliberately the last thing the step does — the batch has
        // been applied and the queue has been pumped, so the sweep sees the pipeline's own result instead of
        // racing it, and paths the batch just touched are still inside the calibration grace window. Who is
        // calibrated — a scope flagged for reconciliation (U3-C) or one whose routine period elapsed (U3-D) —
        // is decided per scope inside; a scope that is not due is not swept at all, so the driver's poll
        // cadence never becomes a per-step disk scan.
        await CalibrateDueScopesAsync(cancellationToken).ConfigureAwait(false);

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
    /// Calibrates every scope whose calibration is <b>due</b>, and clears the flag when the sweep was allowed to
    /// run to the end.
    /// <para>
    /// Two things make a scope due, and only these two (U3-D):
    /// <list type="number">
    ///   <item><description>
    ///     <b>It is flagged for reconciliation</b> (U3-C, unchanged): the first attempt happens on the very next
    ///     step, later attempts are throttled to <see cref="DefaultCalibrationInterval"/> — and, since U3-E, to
    ///     the widening rungs of the backoff ladder above it — so a root that keeps being refused cannot be
    ///     re-probed (nor re-logged) at the driver's poll cadence, nor once a minute for ever either.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Its routine period elapsed</b>: a scope nobody flagged is calibrated once per
    ///     <see cref="DefaultCalibrationPeriod"/> anyway. See that constant for why a routine sweep is the only
    ///     thing that can clear the stale rows of a scope whose change source went quiet for good.
    ///   </description></item>
    /// </list>
    /// Both clocks are read from the scope they belong to, so one scope coming due never calibrates another, and
    /// the period bounds how often a scope is swept — it is never a licence to sweep on every step.
    /// </para>
    /// <para>
    /// A refused sweep (root missing/unreadable) and a truncated one both keep — or set — the scope's flag, so
    /// "I could not tell" is never mistaken for "nothing was stale"; the throttle then bounds what follows.
    /// </para>
    /// </summary>
    private async Task CalibrateDueScopesAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();

        foreach (var (key, entry) in SnapshotScopeEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryBeginCalibration(entry, nowUtc))
                continue;

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

                // U3-E: a run that never got an answer out of the root is a failure like a refusal, so it walks
                // the retry ladder too — otherwise a calibrator that always throws would be re-run every 60 s.
                var failed = RegisterCalibrationFailure(entry);

                _logger?.LogError(ex,
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration failed; the scope stays flagged for reconciliation. Consecutive unusable-root outcome {Failures}; next attempt no earlier than {NextAttemptAtUtc} ({Backoff} after the last attempt).",
                    key.ScopeId, failed.Failures, failed.NextAttemptAtUtc, failed.Interval);
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

                // U3-E: the refusal also widens the gap before the next attempt. One refusal still means the
                // 60 s bound U3-C fixed; it is the second and later ones that back off.
                var refused = RegisterCalibrationFailure(entry);

                _logger?.LogError(
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration was refused ({Reason}); the scope stays flagged and no file was removed. Consecutive unusable-root outcome {Failures}; next attempt no earlier than {NextAttemptAtUtc} ({Backoff} after the last attempt).",
                    key.ScopeId, result.RejectionReason ?? "unspecified", refused.Failures, refused.NextAttemptAtUtc, refused.Interval);
                continue;
            }

            if (result.Truncated)
            {
                // A truncated sweep is not a finished one: the scope is known to hold more stale rows than this
                // run was allowed to remove, so the flag is set — on the routine path too, which is why it is set
                // here instead of merely kept. The retry throttle paces the remaining rounds.
                entry.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.CalibrationTruncated);

                // U3-E: the run did read the root, so the remaining rounds are paced by the base interval again —
                // a long outage before this run must not slow the completion of the sweep it just started.
                ResetCalibrationBackoff(entry, key.ScopeId);

                _logger?.LogWarning(
                    "[CodeIndexMaintenance] Scope {ScopeId}: calibration stopped at the per-run ceiling after sweeping {Swept} file(s); the scope stays flagged for a later run.",
                    key.ScopeId, result.SweptFileCount);
                continue;
            }

            entry.State.ClearNeedsReconcile();

            // U3-E: a sweep that finished is the reset point of the ladder — the next failure starts from 60 s
            // again, whatever the outage before this success cost.
            ResetCalibrationBackoff(entry, key.ScopeId);

            _logger?.LogInformation(
                "[CodeIndexMaintenance] Scope {ScopeId}: calibration swept {Swept} stale file(s) ({Absent} of {Scanned} indexed path(s) are not on disk, {Protected} left alone as recently observed); the reconcile flag is cleared.",
                key.ScopeId, result.SweptFileCount, result.AbsentFileCount, result.ScannedFileCount, result.ProtectedFileCount);
        }
    }

    /// <summary>
    /// Tells whether a calibration of <paramref name="entry"/> is due right now, and — when it is — stamps the
    /// attempt so no later step re-attempts this scope before the relevant interval has elapsed.
    /// <para>Per scope by construction: everything it reads and writes belongs to this entry alone.</para>
    /// </summary>
    /// <param name="entry">Scope to decide about.</param>
    /// <param name="nowUtc">The step's instant (one reading per step, so a step is consistent with itself).</param>
    /// <returns><c>true</c> when the caller must calibrate this scope now.</returns>
    private bool TryBeginCalibration(ScopeEntry entry, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!IsCalibrationDue(entry, nowUtc))
                return false;

            // Both rules are measured from an attempt, so a scope that was just calibrated — for either reason
            // — is not calibrated again by the very next step.
            entry.LastCalibrationAttemptAtUtc = nowUtc;
            return true;
        }
    }

    /// <summary>
    /// The due rule of one scope. Deliberately pure and lock-free: the caller owns the service gate.
    /// </summary>
    private static bool IsCalibrationDue(ScopeEntry entry, DateTimeOffset nowUtc)
    {
        if (entry.State.NeedsReconcile)
        {
            // U3-C, unchanged: flagging a scope means "calibrate it on the very next step"; the retry interval
            // only throttles re-attempts of a scope that stays flagged.
            if (entry.LastCalibrationAttemptAtUtc is not { } lastAttempt)
                return true;

            // U3-E: that interval is now the rung of the backoff ladder the scope sits on. A scope that has not
            // failed yet sits on the first rung, which is exactly the 60 s bound U3-C fixed — so the ladder only
            // ever widens <b>repeated</b> failure, and a scope flagged for the first time is still calibrated on
            // the very next step (that path returns above, before any interval is consulted).
            return nowUtc - lastAttempt >= CalibrationBackoffInterval(entry.ConsecutiveCalibrationFailures);
        }

        // U3-D: the routine cadence, measured from the completion of this scope's previous calibration run
        // (ADR-089 §U3-C: "均按上次完成后计时，不并发叠加"), and from the attach instant while none ever ran.
        // A run that threw never reaches this branch: it leaves the scope flagged, which is what the branch
        // above is for. U3-E deliberately leaves this clock alone: the ladder paces retries of a scope that
        // failed, it is not a second routine clock.
        return nowUtc - (entry.LastCalibrationAtUtc ?? entry.AttachedAtUtc) >= DefaultCalibrationPeriod;
    }

    /// <summary>
    /// The U3-E retry ladder: how long a scope that has failed <paramref name="consecutiveFailures"/> times in a
    /// row must wait before its next calibration attempt.
    /// <para>
    /// <see cref="DefaultCalibrationInterval"/> doubled once per further failure — 60 s, 2 min, 4, 8, 16 — and
    /// capped at <see cref="DefaultCalibrationBackoffMax"/>. Doubling starts at the <b>second</b> failure, so one
    /// refusal still waits the 60 s U3-C fixed and only repeated failure widens the gap.
    /// </para>
    /// <para>
    /// Pure and total: every value maps to an interval, and the loop stops as soon as it reaches the cap, so a
    /// scope that failed a million times costs no more than one that failed six.
    /// </para>
    /// </summary>
    /// <param name="consecutiveFailures">Consecutive unusable-root outcomes for one scope (0 when none).</param>
    internal static TimeSpan CalibrationBackoffInterval(long consecutiveFailures)
    {
        var interval = DefaultCalibrationInterval;

        for (var rung = 1L; rung < consecutiveFailures && interval < DefaultCalibrationBackoffMax; rung++)
        {
            var doubled = interval + interval;
            interval = doubled > DefaultCalibrationBackoffMax ? DefaultCalibrationBackoffMax : doubled;
        }

        return interval;
    }

    /// <summary>
    /// Moves one scope one rung down the U3-E retry ladder after an outcome that showed its root was unusable,
    /// and reports the rung it landed on — how many failures in a row that is, the interval the next attempt must
    /// wait, and the instant it may happen.
    /// </summary>
    /// <param name="entry">Scope that failed.</param>
    /// <returns>The rung the scope is on, for the caller to log.</returns>
    private (long Failures, TimeSpan Interval, DateTimeOffset NextAttemptAtUtc) RegisterCalibrationFailure(ScopeEntry entry)
    {
        lock (_gate)
        {
            entry.ConsecutiveCalibrationFailures++;

            var failures = entry.ConsecutiveCalibrationFailures;
            var interval = CalibrationBackoffInterval(failures);
            var nextAttemptAtUtc = (entry.LastCalibrationAttemptAtUtc ?? _timeProvider.GetUtcNow()) + interval;

            return (failures, interval, nextAttemptAtUtc);
        }
    }

    /// <summary>
    /// Puts one scope back on the first rung of the U3-E retry ladder, because a run got to read the root (a
    /// finished sweep or a truncated one). Logs only when there was something to reset, so a healthy scope is
    /// silent about it.
    /// </summary>
    /// <param name="entry">Scope whose calibration got through.</param>
    /// <param name="scopeId">Scoped id, for the log.</param>
    private void ResetCalibrationBackoff(ScopeEntry entry, string scopeId)
    {
        long previous;

        lock (_gate)
        {
            previous = entry.ConsecutiveCalibrationFailures;
            entry.ConsecutiveCalibrationFailures = 0;
        }

        if (previous == 0)
            return;

        _logger?.LogInformation(
            "[CodeIndexMaintenance] Scope {ScopeId}: calibration read the root again; the retry backoff is reset to {Reset} (it was {Failures} consecutive unusable-root outcome(s)).",
            scopeId, DefaultCalibrationInterval, previous);
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
