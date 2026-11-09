using System.Collections.Immutable;

namespace PuddingAgents.Contracts;

// PA-02A 绑定后端端口：BCL-only。所有身份（workspace/actor/parent work/root budget）由宿主绑定，
// 端口不接收调用方自报的 root/actor/父任务；每张查询/命令都把绑定传下去供适配器复核。

public sealed record AgentBinding(string WorkspaceId, string AgentId, string ActorId, string OwnerWorkId,
    string RootBudgetId, long? ParentMaxOutputTokens = null, decimal? ParentMaxCost = null,
    ImmutableArray<string> AllowedCapabilities = default)
{
    public bool IsValid => Valid(WorkspaceId) && Valid(AgentId) && Valid(ActorId) && Valid(OwnerWorkId)
        && Valid(RootBudgetId) && !AllowedCapabilities.IsDefault;

    public static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

/// <summary>
/// PA-02 I0: read-only caller/workspace scope for directory/config queries. It deliberately carries no Work or root
/// budget: enumeration must not require fabricating an execution identity. The actor is host-bound, never a request field.
/// </summary>
public sealed record AgentReadBinding(string WorkspaceId, string ActorId,
    ImmutableArray<string> AllowedCapabilities = default)
{
    public bool IsValid => AgentBinding.Valid(WorkspaceId) && AgentBinding.Valid(ActorId);
}
public sealed record AgentDirectoryRecord(string WorkspaceId, string AgentId, string DisplayName, string TemplateId,
    int TemplateVersion, long Revision, bool Archived, DateTimeOffset UpdatedAt);

public sealed record AgentDirectoryQueryResult(ImmutableArray<AgentDirectoryRecord> Agents, string? Cursor, bool Complete);

public interface IAgentDirectoryStore
{
    /// <summary>A truncated page returns Complete=false; an empty truncated page must never be reported as "not found".</summary>
    ValueTask<AgentDirectoryQueryResult> ListAsync(string workspaceId, string? cursor, int maxCount, CancellationToken ct = default);
    ValueTask<AgentDirectoryRecord?> GetAsync(string workspaceId, string agentId, CancellationToken ct = default);
}

public sealed record WorkEventRecord(long Sequence, string Kind, DateTimeOffset OccurredAt, string? ReasonCode,
    string? PayloadRef, string? PayloadHash);

public sealed record WorkEventPage(ImmutableArray<WorkEventRecord> Events, long NextSequence, string Status,
    bool Complete, bool SnapshotRequired, long OldestAvailableSequence);

/// <summary>Committed work events only. A cursor that predates retention must return SnapshotRequired, never silently skip.</summary>
public interface IWorkEventSource
{
    ValueTask<WorkEventPage> ReadAsync(string workspaceId, string operationId, long afterSequence, int maxCount,
        CancellationToken ct = default);
}

public enum CancelWorkEffect { Requested, AlreadyTerminal, NotFound, Forbidden }

public sealed record WorkCancelOutcome(CancelWorkEffect Effect, long Revision, string? CurrentStatus);

/// <summary>Calls the existing canonical cancel entry; Requested never claims the provider stopped.</summary>
public interface IWorkCancellationGateway
{
    ValueTask<WorkCancelOutcome> CancelAsync(string workspaceId, string targetOperationId, string reason,
        CancellationToken ct = default);
}

public sealed record CancelCommandRecord(string OperationId, string Fingerprint, CancelWorkEffect Effect,
    long Revision, string? CurrentStatus);

/// <summary>Idempotent cancel acceptance: the same operation id with the same content returns the original record.</summary>
public interface ICancelOperationStore
{
    ValueTask<CancelCommandRecord?> GetAsync(string operationId, CancellationToken ct = default);
    ValueTask SaveAsync(CancelCommandRecord record, CancellationToken ct = default);
}

public sealed record ChildWorkCommand(string OperationId, string ParentOperationId, string WorkspaceId, string AgentId,
    string Text, ImmutableArray<string> Capabilities, long? MaxOutputTokens, decimal? MaxCost, string DeliverMode);

/// <summary>Reuses the canonical child-work backend; the parent identity always comes from the binding.</summary>
public interface IChildWorkSubmitter
{
    ValueTask<DelegationResult> SubmitAsync(ChildWorkCommand command, CancellationToken ct = default);
}

public sealed record DelegationResult(string ChildOperationId, string ParentOperationId, string State,
    DateTimeOffset AcceptedAt);

public enum LeaseAcquireStatus { Acquired, Busy, Unavailable }

public sealed record LeaseAcquireCommand(string OperationId, string WorkspaceId, string AgentId,
    ImmutableArray<string> RequiredCapabilities, string OwnerWorkId, long ParentMaxOutputTokens, decimal ParentMaxCost,
    DateTimeOffset Deadline);

public sealed record LeaseAcquireResult(LeaseAcquireStatus Status, string? LeaseId, long Generation,
    DateTimeOffset ExpiresAt);

/// <summary>Atomic acquire/release under one domain lock or storage CAS. Release is fenced by owner and generation.</summary>
public interface ISubAgentLeasePool
{
    ValueTask<LeaseAcquireResult> AcquireAsync(LeaseAcquireCommand command, CancellationToken ct = default);
    ValueTask<bool> ReleaseAsync(string leaseId, long generation, string ownerWorkId, CancellationToken ct = default);
}

public static class AgentErrorCodes
{
    public const string RequestInvalid = "agent.request_invalid";
    public const string BindingInvalid = "agent.binding_invalid";
    public const string Forbidden = "agent.forbidden";
    public const string NotFound = "agent.not_found";
    public const string OperationConflict = "agent.operation_conflict";
    public const string LeaseBusy = "agent.lease_busy";
    public const string LeaseUnavailable = "agent.lease_unavailable";
    public const string LeaseReleaseRejected = "agent.lease_release_rejected";
    public const string ChildLimitExceedsParent = "agent.child_limit_exceeds_parent";
    public const string CapabilityDenied = "agent.capability_denied";
    public const string SnapshotRequired = "agent.snapshot_required";
}

public sealed class AgentBackendException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
