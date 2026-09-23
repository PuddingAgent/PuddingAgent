namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Explicit, externally driven pump for an <see cref="ICodeIndexScheduler"/>.
/// <para>
/// The index scheduler owns <b>no</b> background loop (ADR-089 §2 "驱动归属, 2026-09-23"): a single
/// driver — today <c>CodeIndexMaintenanceService</c>, later the Host-level hosted service — advances the
/// queue by calling <see cref="ProcessPendingAsync"/>. That removes the double-writer window between a
/// self-started worker inside the scheduler and the host-level driver.
/// </para>
/// <para>
/// The existing <see cref="ICodeIndexScheduler"/> members keep their meaning unchanged; this interface
/// only adds the pump and per-scope version water marks.
/// </para>
/// </summary>
public interface ICodeIndexSchedulerDriver : ICodeIndexScheduler
{
    /// <summary>
    /// Runs every queued indexing job until the queue is empty.
    /// <para>
    /// A scope that is marked again while it is being processed is re-queued and picked up by the same
    /// call, so a change that arrives during indexing is never lost. A job cancelled through
    /// <paramref name="cancellationToken"/> is put back on the queue instead of being dropped.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the pump; already queued jobs stay queued.</param>
    /// <returns>
    /// Number of indexing runs that actually executed and committed. A scope that was skipped (missing or
    /// removed), a scope whose workspace could not be resolved, and a cancelled run are all excluded —
    /// the caller must not read them as "indexing happened".
    /// </returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-scope version water marks for a single scope. Unknown scopes report zeroed marks instead of
    /// throwing, so observability is total.
    /// </summary>
    /// <param name="workspaceId">Workspace that owns the scope.</param>
    /// <param name="scopeId">Scope to report.</param>
    CodeIndexScopeProgress GetProgress(string workspaceId, string scopeId);

    /// <summary>Per-scope version water marks for every scope this driver has seen.</summary>
    IReadOnlyList<CodeIndexScopeProgress> GetProgress();
}

/// <summary>
/// Version water marks and coalescing counters of one scope, as observed by the index scheduler driver.
/// </summary>
/// <param name="WorkspaceId">Workspace that owns the scope.</param>
/// <param name="ScopeId">Scope the marks belong to.</param>
/// <param name="DesiredVersion">
/// Number of indexing requests accepted for the scope (monotonic for the lifetime of the scheduler).
/// It advances on <b>every</b> accepted request, including requests that arrive while the scope is being
/// indexed.
/// </param>
/// <param name="CommittedVersion">Highest desired version an indexing run has committed.</param>
/// <param name="Pending">True when a job for the scope is queued and not yet running.</param>
/// <param name="InFlight">True while an indexing run for the scope is executing.</param>
/// <param name="MarkedWhileInFlightCount">
/// Number of requests that arrived while the scope was being indexed. These are the requests the old
/// <c>_inFlight.Contains(...) =&gt; return</c> shortcut used to discard.
/// </param>
public sealed record CodeIndexScopeProgress(
    string WorkspaceId,
    string ScopeId,
    long DesiredVersion,
    long CommittedVersion,
    bool Pending,
    bool InFlight,
    long MarkedWhileInFlightCount)
{
    /// <summary>True when the last committed version is behind the desired version.</summary>
    public bool Behind => CommittedVersion < DesiredVersion;
}
