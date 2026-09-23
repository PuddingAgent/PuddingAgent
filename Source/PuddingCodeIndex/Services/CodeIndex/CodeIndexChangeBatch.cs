namespace PuddingCodeIntelligence.Services.CodeIndex;

/// <summary>
/// A coalesced unit of work produced by <see cref="CodeIndexChangeCoalescer"/>: the set of paths that
/// must be re-read and the set of paths that must be removed, plus the version the batch covers.
/// <para>
/// The batch deliberately carries <b>no</b> file fingerprints and no staleness verdict — fingerprinting
/// and "is this batch obsolete?" belong to the incremental-commit slice (U3-B).
/// </para>
/// </summary>
/// <param name="WorkspaceId">Workspace that owns the scope.</param>
/// <param name="ScopeId">Scope the batch belongs to.</param>
/// <param name="Version">Highest observation sequence covered by this batch.</param>
/// <param name="PathsToReindex">Deduplicated absolute paths whose final state must be re-read.</param>
/// <param name="PathsToRemove">Deduplicated absolute paths that no longer exist.</param>
/// <param name="ReconcileRequired">
/// True when fine-grained information is no longer trustworthy for this batch (queue overflow, watcher
/// error or path-budget collapse) and a full reconciliation of the scope is required.
/// </param>
/// <param name="ReconcileReason">Reason behind <paramref name="ReconcileRequired"/>.</param>
/// <param name="CreatedAtUtc">When the batch was produced.</param>
public sealed record CodeIndexChangeBatch(
    string WorkspaceId,
    string ScopeId,
    long Version,
    IReadOnlyList<string> PathsToReindex,
    IReadOnlyList<string> PathsToRemove,
    bool ReconcileRequired,
    string? ReconcileReason,
    DateTimeOffset CreatedAtUtc);
