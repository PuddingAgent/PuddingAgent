using PuddingCode.Runtime;

namespace PuddingCode.Orchestration;

/// <summary>Result of an optimistic write to the orchestration run store.</summary>
public enum SubAgentOrchestrationStoreWriteStatus
{
    Succeeded,
    AlreadyExists,
    NotFound,
    VersionConflict
}

/// <summary>Optimistic store result, including the current value after a rejected write.</summary>
public sealed record SubAgentOrchestrationStoreWriteResult
{
    public required SubAgentOrchestrationStoreWriteStatus Status { get; init; }
    public SubAgentOrchestrationRunSnapshot? CurrentSnapshot { get; init; }
    public bool Success => Status == SubAgentOrchestrationStoreWriteStatus.Succeeded;
}

/// <summary>
/// Persistence boundary for MOA run snapshots. Implementations must use Version as an optimistic
/// concurrency token so that the same ready work cannot be claimed by two dispatchers.
/// </summary>
public interface ISubAgentOrchestrationRunStore
{
    Task<SubAgentOrchestrationRunSnapshot?> GetAsync(string runId, CancellationToken ct = default);

    Task<SubAgentOrchestrationStoreWriteResult> TryCreateAsync(
        SubAgentOrchestrationRunSnapshot snapshot,
        CancellationToken ct = default);

    Task<SubAgentOrchestrationStoreWriteResult> TryUpdateAsync(
        SubAgentOrchestrationRunSnapshot snapshot,
        long expectedVersion,
        CancellationToken ct = default);
}

/// <summary>Optional execution metadata supplied when ready MOA work is dispatched.</summary>
public sealed record DesignCouncilDispatchRequest
{
    public required string RunId { get; init; }
    public int RequestedCount { get; init; } = 1;
    public string? WorkingDirectory { get; init; }
    public int? MaxRounds { get; init; }
    public int? TimeoutSeconds { get; init; }
    public DateTimeOffset? ParentExecutionDeadlineUtc { get; init; }
    public RuntimeExecutionIdentity? ParentExecutionIdentity { get; init; }
}

/// <summary>Observed outcome of one claimed child-agent invocation.</summary>
public sealed record DesignCouncilDispatchedWorkItemResult
{
    public required string WorkItemId { get; init; }
    public required string ClaimId { get; init; }
    public required SubAgentWorkItemOutcome Outcome { get; init; }
    public string? ExternalRunId { get; init; }
    public string? ExternalSubSessionId { get; init; }
    public bool CompletionPersisted { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result shared by create, activate, resume, and cancel commands.</summary>
public sealed record DesignCouncilRunCommandResult
{
    public SubAgentOrchestrationRunSnapshot? Snapshot { get; init; }
    public IReadOnlyList<SubAgentOrchestrationOperationIssue> Issues { get; init; } =
        Array.Empty<SubAgentOrchestrationOperationIssue>();
    public bool Success => Snapshot is not null && Issues.Count == 0;
}

/// <summary>Result of claiming, invoking, and recording a batch of ready work.</summary>
public sealed record DesignCouncilDispatchResult
{
    public SubAgentOrchestrationRunSnapshot? Snapshot { get; init; }
    public IReadOnlyList<DesignCouncilDispatchedWorkItemResult> WorkItems { get; init; } =
        Array.Empty<DesignCouncilDispatchedWorkItemResult>();
    public IReadOnlyList<SubAgentOrchestrationOperationIssue> Issues { get; init; } =
        Array.Empty<SubAgentOrchestrationOperationIssue>();
    public bool Success => Snapshot is not null && Issues.Count == 0 && WorkItems.All(item => item.CompletionPersisted);
}

/// <summary>
/// Runtime facade for the design council. It persists every accepted state transition before
/// dispatch and delegates actual child execution to the existing sub-agent invocation service.
/// </summary>
public interface IDesignCouncilRuntimeService
{
    Task<SubAgentOrchestrationRunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default);

    Task<DesignCouncilRunCommandResult> CreateRunAsync(
        SubAgentOrchestrationPlan plan,
        string runId,
        CancellationToken ct = default);

    Task<DesignCouncilRunCommandResult> ActivateAsync(string runId, CancellationToken ct = default);

    Task<DesignCouncilDispatchResult> DispatchReadyAsync(
        DesignCouncilDispatchRequest request,
        CancellationToken ct = default);

    Task<DesignCouncilRunCommandResult> ResumeAsync(
        string runId,
        SubAgentOrchestrationContextResolution resolution,
        CancellationToken ct = default);

    Task<DesignCouncilRunCommandResult> CancelAsync(
        string runId,
        string reason,
        CancellationToken ct = default);
}
