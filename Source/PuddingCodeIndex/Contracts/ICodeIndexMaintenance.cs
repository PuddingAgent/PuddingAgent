namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Lifecycle and read-only observability of the change-driven index maintenance driver (U3-B1).
/// <para>
/// The implementation owns one change-capture pipeline per scope (watcher, bounded queue, scope state,
/// coalescer) and turns every coalesced batch into a real indexing run. It is deliberately
/// <b>not</b> an <c>IHostedService</c>: hosting/lifecycle wiring belongs to the Host (U3-B2) and the
/// component must not depend on it.
/// </para>
/// </summary>
public interface ICodeIndexMaintenance
{
    /// <summary>Starts the driver. Idempotent.</summary>
    /// <param name="cancellationToken">Cancels the start request itself.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the driver: change sources are detached so no new observation can be published, the loop is
    /// cancelled and awaited <b>within a bounded</b> timeout. Never blocks indefinitely.
    /// </summary>
    /// <param name="cancellationToken">Cancels the stop request itself.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Number of coalesced batches handled since construction.</summary>
    long BatchesProcessed { get; }

    /// <summary>Number of handled batches that carried <c>ReconcileRequired</c>.</summary>
    long ReconcileRequests { get; }

    /// <summary>Number of removal paths carried by handled batches (never silently swallowed).</summary>
    long RemovalObservations { get; }

    /// <summary>Number of scopes currently flagged as needing reconciliation.</summary>
    int PendingReconcileScopeCount { get; }

    /// <summary>Status of every scope currently attached to the driver.</summary>
    IReadOnlyList<CodeIndexMaintenanceScopeStatus> GetScopeStatuses();

    /// <summary>Status of one attached scope, or null when the scope is not attached.</summary>
    /// <param name="workspaceId">Workspace that owns the scope.</param>
    /// <param name="scopeId">Scope to report.</param>
    CodeIndexMaintenanceScopeStatus? GetScopeStatus(string workspaceId, string scopeId);
}

/// <summary>Read-only status of one scope attached to the maintenance driver.</summary>
/// <param name="WorkspaceId">Workspace that owns the scope.</param>
/// <param name="ScopeId">Scope these numbers belong to.</param>
/// <param name="RootPath">Root path the scope is watched at.</param>
/// <param name="ObservedVersion">Monotonic count of observations recorded by the scope state.</param>
/// <param name="DesiredVersion">Indexing requests accepted for the scope (from the scheduler driver).</param>
/// <param name="CommittedVersion">Highest indexing request an indexing run committed.</param>
/// <param name="MarkedWhileInFlightCount">Requests that arrived while the scope was being indexed.</param>
/// <param name="IndexPending">True while an indexing job for the scope is queued.</param>
/// <param name="IndexInFlight">True while an indexing run for the scope is executing.</param>
/// <param name="NeedsReconcile">
/// True when fine-grained capture can no longer be trusted for the scope. This slice only <b>exposes</b>
/// the flag; clearing it belongs to the calibration slice (U3-C).
/// </param>
/// <param name="ReconcileReason">Reason of the latest reconcile trigger, or null.</param>
/// <param name="ReconcileRequestCount">Number of batches handled for this scope that required reconciliation.</param>
/// <param name="RemovalObservationCount">Number of removal paths observed for this scope (cumulative).</param>
/// <param name="LastRemovalPaths">Removal paths of the most recent batch (per-file removal is U3-B3).</param>
/// <param name="WatcherAttached">True when a change source is attached to the scope.</param>
public sealed record CodeIndexMaintenanceScopeStatus(
    string WorkspaceId,
    string ScopeId,
    string RootPath,
    long ObservedVersion,
    long DesiredVersion,
    long CommittedVersion,
    long MarkedWhileInFlightCount,
    bool IndexPending,
    bool IndexInFlight,
    bool NeedsReconcile,
    string? ReconcileReason,
    long ReconcileRequestCount,
    long RemovalObservationCount,
    IReadOnlyList<string> LastRemovalPaths,
    bool WatcherAttached);
