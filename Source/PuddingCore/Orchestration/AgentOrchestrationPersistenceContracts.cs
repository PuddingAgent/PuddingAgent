namespace PuddingCode.Orchestration;

/// <summary>Outcome of a durable orchestration store command.</summary>
public enum AgentOrchestrationStoreStatus
{
    Applied,
    Unchanged,
    Conflict,
    NotFound,
    InvalidState,
    NoWork
}

/// <summary>Stable result envelope for idempotent/CAS persistence commands.</summary>
public sealed record AgentOrchestrationStoreResult<T>
    where T : class
{
    public required AgentOrchestrationStoreStatus Status { get; init; }
    public T? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long? CurrentVersion { get; init; }
    /// <summary>Current head revision id for CAS conflicts; null when not applicable.</summary>
    public string? CurrentRevisionId { get; init; }
    /// <summary>Compiler diagnostics for definition failures; empty for other failure classes.</summary>
    public IReadOnlyList<AgentOrchestrationValidationIssue> Issues { get; init; }
        = Array.Empty<AgentOrchestrationValidationIssue>();
    public bool Success => Status is AgentOrchestrationStoreStatus.Applied or AgentOrchestrationStoreStatus.Unchanged;
}

/// <summary>CAS request for an immutable graph revision.</summary>
public sealed record AgentOrchestrationRevisionWriteRequest
{
    public required AgentOrchestrationGraphDefinition Definition { get; init; }

    /// <summary>Zero creates a graph; later revisions must match its current revision number.</summary>
    public int ExpectedCurrentRevision { get; init; }
}

/// <summary>CAS request for deleting an unexecuted graph and its editor-only state.</summary>
public sealed record AgentOrchestrationGraphDeleteRequest
{
    public required string GraphId { get; init; }
    public int ExpectedCurrentRevision { get; init; }
}

/// <summary>Audit-friendly result of deleting a graph that has no durable runs.</summary>
public sealed record AgentOrchestrationGraphDeleteReceipt
{
    public required string GraphId { get; init; }
    public int PreviousRevision { get; init; }
    public int DeletedRevisionCount { get; init; }
    public int DeletedLayoutCount { get; init; }
}

/// <summary>Read-only metadata for one immutable graph revision.</summary>
public sealed record AgentOrchestrationRevisionSummary
{
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public int Revision { get; init; }
    public string? ParentRevisionId { get; init; }
    public required string SchemaVersion { get; init; }
    public required string ContentHash { get; init; }
    public required string CreatedByAgentId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Current graph head plus lightweight run activity for Admin discovery.</summary>
public sealed record AgentOrchestrationGraphSummary
{
    public required string GraphId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootSessionId { get; init; }
    public required string CreatedByAgentId { get; init; }
    public required string Objective { get; init; }
    public int CurrentRevision { get; init; }
    public required string CurrentRevisionId { get; init; }
    public int RunCount { get; init; }
    public int ActiveRunCount { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Lightweight run projection used by discovery pages; node details remain on GetRun.</summary>
public sealed record AgentOrchestrationRunSummary
{
    public required string RunId { get; init; }
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootSessionId { get; init; }
    public required string RequestedByAgentId { get; init; }
    public AgentOrchestrationRunStatus Status { get; init; }
    public long Version { get; init; }
    public long HeadSequence { get; init; }
    public int MaxConcurrency { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>CAS request for editor-only layout state; it never changes executable graph content.</summary>
public sealed record AgentOrchestrationLayoutWriteRequest
{
    public required AgentOrchestrationGraphLayout Layout { get; init; }

    /// <summary>Zero creates the first layout; updates must match the current layout revision.</summary>
    public int ExpectedCurrentLayoutRevision { get; init; }
}

/// <summary>Idempotent request to create a run pinned to one graph revision.</summary>
public sealed record AgentOrchestrationRunCreateRequest
{
    public required string RunId { get; init; }
    public required string RevisionId { get; init; }
    public required string RequestedByAgentId { get; init; }
}

/// <summary>Optimistic-concurrency request to activate a draft run.</summary>
public sealed record AgentOrchestrationRunActivationRequest
{
    public required string RunId { get; init; }
    public long ExpectedVersion { get; init; }
}

/// <summary>Atomic request for the next ready node, bounded by the graph concurrency limit.</summary>
public sealed record AgentOrchestrationNodeClaimRequest
{
    public required string RunId { get; init; }
    public required string WorkerId { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public long? ExpectedRunVersion { get; init; }
}

/// <summary>Fenced ownership of one node attempt.</summary>
public sealed record AgentOrchestrationNodeClaim
{
    public required string RunId { get; init; }
    public required string NodeId { get; init; }
    public required string ClaimId { get; init; }
    public required string WorkerId { get; init; }
    public int Attempt { get; init; }
    public long FencingToken { get; init; }
    public DateTimeOffset LeaseExpiresAtUtc { get; init; }
    public long RunVersion { get; init; }
}

/// <summary>Renews a live claim without changing the node execution identity.</summary>
public sealed record AgentOrchestrationClaimRenewalRequest
{
    public required string RunId { get; init; }
    public required string NodeId { get; init; }
    public required string ClaimId { get; init; }
    public required string WorkerId { get; init; }
    public long FencingToken { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Binds a claimed node to the actual immutable child execution run.</summary>
public sealed record AgentOrchestrationNodeStartRequest
{
    public required string RunId { get; init; }
    public required string NodeId { get; init; }
    public required string ClaimId { get; init; }
    public required string WorkerId { get; init; }
    public long FencingToken { get; init; }
    public required string ExecutionRunId { get; init; }
    public string? SubSessionId { get; init; }
}

/// <summary>Fenced terminal commit for one claimed/running node.</summary>
public sealed record AgentOrchestrationNodeTerminalRequest
{
    public required string RunId { get; init; }
    public required string NodeId { get; init; }
    public required string ClaimId { get; init; }
    public required string WorkerId { get; init; }
    public long FencingToken { get; init; }
    public bool Succeeded { get; init; }
    public string? Summary { get; init; }
    public string? ArtifactReference { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Durable current-state projection for one orchestration node.</summary>
public sealed record AgentOrchestrationNodeRunSnapshot
{
    public required string NodeId { get; init; }
    public required AgentOrchestrationNodeKind Kind { get; init; }
    public AgentOrchestrationNodeRunStatus Status { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public string? ClaimId { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; init; }
    public long FencingToken { get; init; }
    public string? ExecutionRunId { get; init; }
    public string? SubSessionId { get; init; }
    public string? OutputSummary { get; init; }
    public string? ArtifactReference { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Durable current-state projection for a run pinned to an immutable revision.</summary>
public sealed record AgentOrchestrationRunSnapshot
{
    public required string RunId { get; init; }
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootSessionId { get; init; }
    public required string RequestedByAgentId { get; init; }
    public AgentOrchestrationRunStatus Status { get; init; }
    public long Version { get; init; }
    public long HeadSequence { get; init; }
    public int MaxConcurrency { get; init; }
    public IReadOnlyList<AgentOrchestrationNodeRunSnapshot> Nodes { get; init; }
        = Array.Empty<AgentOrchestrationNodeRunSnapshot>();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Read-only durable definition, projection, and append-only event queries.</summary>
public interface IAgentOrchestrationQueryStore
{
    Task<IReadOnlyList<AgentOrchestrationGraphSummary>> ListGraphsAsync(
        string? workspaceId,
        int limit,
        int offset,
        CancellationToken ct = default);

    Task<AgentOrchestrationGraphDefinition?> GetRevisionAsync(
        string revisionId,
        CancellationToken ct = default);

    Task<AgentOrchestrationGraphDefinition?> GetLatestRevisionAsync(
        string graphId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentOrchestrationRevisionSummary>> ListRevisionsAsync(
        string graphId,
        int limit,
        CancellationToken ct = default);

    Task<AgentOrchestrationRunSnapshot?> GetRunAsync(
        string runId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentOrchestrationRunSummary>> ListRunsAsync(
        string? workspaceId,
        string? graphId,
        AgentOrchestrationRunStatus? status,
        int limit,
        int offset,
        CancellationToken ct = default);

    Task<AgentOrchestrationGraphLayout?> GetLayoutAsync(
        string graphId,
        string baseRevisionId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentOrchestrationRunEvent>> GetEventsAfterAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken ct = default);
}

/// <summary>
/// Durable graph/run command store. Every state transition that emits events commits state and event
/// rows in one transaction; live notification occurs only after that transaction succeeds.
/// </summary>
public interface IAgentOrchestrationStore : IAgentOrchestrationQueryStore
{
    Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphDefinition>> SaveRevisionAsync(
        AgentOrchestrationRevisionWriteRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphDeleteReceipt>> DeleteGraphAsync(
        AgentOrchestrationGraphDeleteRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphLayout>> SaveLayoutAsync(
        AgentOrchestrationLayoutWriteRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> CreateRunAsync(
        AgentOrchestrationRunCreateRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> ActivateRunAsync(
        AgentOrchestrationRunActivationRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>> TryClaimNextReadyNodeAsync(
        AgentOrchestrationNodeClaimRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>> RenewClaimAsync(
        AgentOrchestrationClaimRenewalRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> MarkNodeRunningAsync(
        AgentOrchestrationNodeStartRequest request,
        CancellationToken ct = default);

    Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> CommitNodeTerminalAsync(
        AgentOrchestrationNodeTerminalRequest request,
        CancellationToken ct = default);
}

/// <summary>Wake-up signal only; consumers must read committed events from the durable store.</summary>
public interface IAgentOrchestrationCommittedEventSignal
{
    ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct);
    void Signal(string runId, long committedThroughSequence);
}
