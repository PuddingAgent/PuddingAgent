namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Per-scope mutable observation state for the index change pipeline.
/// <para>
/// Thread-safe and deliberately <b>independent of</b> <see cref="CodeIndexChangeQueue"/>: facts such as
/// "dirty", "overflowed" and "needs reconcile" must still be recordable while the queue is full —
/// which is precisely the failure mode this slice exists to handle. Recording a fact therefore never
/// depends on the queue having room.
/// </para>
/// <para>Contains no IO and no reference to the queue, the store or any index writer.</para>
/// </summary>
public sealed class CodeIndexScopeState
{
    /// <summary>Well-known reasons a scope has been marked as needing full reconciliation.</summary>
    public static class ReconcileReasons
    {
        /// <summary>The bounded change queue rejected an observation because it was full.</summary>
        public const string QueueOverflow = "queue_overflow";

        /// <summary>
        /// <see cref="FileSystemWatcher"/> raised an <c>Error</c> event. Watcher internal-buffer overflows
        /// are delivered through the same event (as <see cref="System.IO.InternalBufferOverflowException"/>)
        /// and additionally increment <see cref="OverflowCount"/>.
        /// </summary>
        public const string WatcherError = "watcher_error";

        /// <summary>The coalescer's pending-path budget was exceeded and fine-grained work was collapsed.</summary>
        public const string PathLimitExceeded = "path_limit_exceeded";

        /// <summary>An unexpected failure occurred inside a watcher callback.</summary>
        public const string CallbackFault = "callback_fault";

        /// <summary>A batch could not be applied to the index (store/indexer failure); the scope must be reconciled.</summary>
        public const string BatchApplicationFailed = "batch_application_failed";

        /// <summary>
        /// Calibration (U3-C) was refused because the scope root is missing or unreadable, so "this path is
        /// gone" could not be trusted for any path. The flag stays set and the sweep is retried later.
        /// </summary>
        public const string CalibrationRootUnavailable = "calibration_root_unavailable";

        /// <summary>Calibration (U3-C) itself failed unexpectedly; the scope stays flagged for a later run.</summary>
        public const string CalibrationFailed = "calibration_failed";
    }

    private readonly object _gate = new();

    private string _workspaceId;
    private string _scopeId;
    private long _observedVersion;
    private long _overflowCount;
    private long _droppedChangeCount;
    private bool _dirty;
    private bool _needsReconcile;
    private string? _reconcileReason;
    private DateTimeOffset? _lastObservedAtUtc;

    /// <summary>Creates a state holder for one scope.</summary>
    public CodeIndexScopeState(string workspaceId, string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        _workspaceId = workspaceId;
        _scopeId = scopeId;
    }

    /// <summary>Workspace that owns this scope.</summary>
    public string WorkspaceId
    {
        get { lock (_gate) { return _workspaceId; } }
    }

    /// <summary>Scope this state belongs to.</summary>
    public string ScopeId
    {
        get { lock (_gate) { return _scopeId; } }
    }

    /// <summary>True while observations have been made that no consumer has fully accounted for yet.</summary>
    public bool Dirty
    {
        get { lock (_gate) { return _dirty; } }
    }

    /// <summary>Monotonically increasing count of observed changes. Never decreases; never reset by <see cref="Reset()"/>.</summary>
    public long ObservedVersion
    {
        get { lock (_gate) { return _observedVersion; } }
    }

    /// <summary>True when fine-grained capture can no longer be trusted and a full reconciliation is required.</summary>
    public bool NeedsReconcile
    {
        get { lock (_gate) { return _needsReconcile; } }
    }

    /// <summary>Reason of the most recent reconcile trigger, or null when no reconcile is needed.</summary>
    public string? ReconcileReason
    {
        get { lock (_gate) { return _reconcileReason; } }
    }

    /// <summary>Number of observations dropped because the bounded queue was full (or the watcher buffer overflowed).</summary>
    public long OverflowCount
    {
        get { lock (_gate) { return _overflowCount; } }
    }

    /// <summary>Number of observed changes that could not be published for this scope.</summary>
    public long DroppedChangeCount
    {
        get { lock (_gate) { return _droppedChangeCount; } }
    }

    /// <summary>Timestamp of the most recently observed change, or null when nothing was observed yet.</summary>
    public DateTimeOffset? LastObservedAtUtc
    {
        get { lock (_gate) { return _lastObservedAtUtc; } }
    }

    /// <summary>
    /// Allocates the next observation sequence. The returned value is strictly increasing for the
    /// lifetime of this state holder, so a batch can later advance only the versions it really handled.
    /// </summary>
    public long NextSequence()
    {
        lock (_gate)
        {
            _observedVersion++;
            return _observedVersion;
        }
    }

    /// <summary>Records that a change was observed at <paramref name="observedAtUtc"/> and marks the scope dirty.</summary>
    public void MarkObserved(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _dirty = true;
            _lastObservedAtUtc = observedAtUtc;
        }
    }

    /// <summary>
    /// Marks the scope as needing full reconciliation. Safe to call repeatedly — the latest reason wins
    /// and the flag stays set until <see cref="Reset()"/> (reconcile completion is owned by a later slice).
    /// </summary>
    /// <returns><c>true</c> when the flag transitioned from clear to set.</returns>
    public bool MarkNeedsReconcile(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            var wasSet = _needsReconcile;
            _needsReconcile = true;
            _reconcileReason = reason;
            return !wasSet;
        }
    }

    /// <summary>Increments <see cref="OverflowCount"/>.</summary>
    /// <returns>The new total.</returns>
    public long RecordOverflow(long count = 1)
    {
        lock (_gate)
        {
            _overflowCount += count;
            return _overflowCount;
        }
    }

    /// <summary>Increments <see cref="DroppedChangeCount"/>.</summary>
    /// <returns>The new total.</returns>
    public long RecordDroppedChange(long count = 1)
    {
        lock (_gate)
        {
            _droppedChangeCount += count;
            return _droppedChangeCount;
        }
    }

    /// <summary>
    /// Clears the reconcile flag after a successful calibration (U3-C).
    /// <para>
    /// Only "fine-grained capture can no longer be trusted" is resolved here: the dirty flag, the observation
    /// version and every cumulative counter are deliberately left alone, because a calibration says nothing
    /// about whether an observed change still has to be indexed. Clearing everything is <see cref="Reset()"/>.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when the flag was set and is now clear.</returns>
    public bool ClearNeedsReconcile()
    {
        lock (_gate)
        {
            var wasSet = _needsReconcile;
            _needsReconcile = false;
            _reconcileReason = null;
            return wasSet;
        }
    }

    /// <summary>
    /// Clears the dirty and reconcile flags after a successful reconciliation.
    /// Counters and <see cref="ObservedVersion"/> are cumulative observability values and are intentionally kept.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _dirty = false;
            _needsReconcile = false;
            _reconcileReason = null;
        }
    }

    /// <summary>
    /// Clears the dirty and reconcile flags and re-stamps the scope identity
    /// (used when a scope is rebound to a new workspace/scope id). Cumulative counters are kept.
    /// </summary>
    public void Reset(string workspaceId, string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        lock (_gate)
        {
            _workspaceId = workspaceId;
            _scopeId = scopeId;
            _dirty = false;
            _needsReconcile = false;
            _reconcileReason = null;
        }
    }

    /// <summary>Takes an immutable point-in-time snapshot, ready for <c>code_index_status</c> style reporting.</summary>
    public CodeIndexScopeStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new CodeIndexScopeStateSnapshot(
                _workspaceId,
                _scopeId,
                _dirty,
                _observedVersion,
                _needsReconcile,
                _reconcileReason,
                _overflowCount,
                _droppedChangeCount,
                _lastObservedAtUtc);
        }
    }
}

/// <summary>Immutable snapshot of <see cref="CodeIndexScopeState"/>.</summary>
public sealed record CodeIndexScopeStateSnapshot(
    string WorkspaceId,
    string ScopeId,
    bool Dirty,
    long ObservedVersion,
    bool NeedsReconcile,
    string? ReconcileReason,
    long OverflowCount,
    long DroppedChangeCount,
    DateTimeOffset? LastObservedAtUtc);
