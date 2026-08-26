namespace PuddingCode.Scheduling;

/// <summary>
/// Durable, conservative availability used by automatic work admission.
/// Missing or stale facts are <see cref="Unknown"/>, never implicitly idle.
/// </summary>
public enum AgentAvailabilityState
{
    Unknown = 0,
    Offline = 1,
    Idle = 2,
    Reserved = 3,
    Busy = 4,
    Frozen = 5,
}

/// <summary>
/// Explains why an Agent cannot accept a new automatic task.  This is kept
/// separate from occupancy: waiting for a child Agent is still Busy work.
/// </summary>
public enum AgentActivityReason
{
    None = 0,
    RuntimeExecution = 1,
    QueuedMessage = 2,
    TaskExecution = 3,
    WaitingSubAgent = 4,
    WaitingApproval = 5,
    WaitingUserInput = 6,
    RetryBackoff = 7,
    SleepUntil = 8,
    Cooling = 9,
    ConfigurationMissing = 10,
    AgentDisabled = 11,
    AgentFrozen = 12,
    ActiveReservation = 13,
}

public sealed record AgentAvailabilitySnapshot
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required AgentAvailabilityState State { get; init; }
    public required AgentActivityReason ActivityReason { get; init; }
    public required long Version { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required DateTimeOffset ValidUntilUtc { get; init; }
    public DateTimeOffset? IdleSinceUtc { get; init; }
    public string? MainConversationId { get; init; }
    public string? ActiveTurnId { get; init; }
    public string? ActiveExecutionId { get; init; }
    public string? ActiveTaskId { get; init; }
    public string? ActiveGoalRunId { get; init; }
    public string? ActiveSubAgentRunId { get; init; }
    public string? ReservationId { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public required string ReasonCode { get; init; }

    public bool IsFresh(DateTimeOffset now) => ValidUntilUtc > now;

    /// <summary>
    /// Only a fresh, fact-backed idle snapshot with no logical work ownership
    /// can accept a new automatic task.
    /// </summary>
    public bool CanAcceptAutomaticTask(DateTimeOffset now) =>
        State == AgentAvailabilityState.Idle
        && ActivityReason == AgentActivityReason.None
        && IsFresh(now)
        && ActiveTurnId is null
        && ActiveExecutionId is null
        && ActiveTaskId is null
        && ActiveGoalRunId is null
        && ActiveSubAgentRunId is null
        && ReservationId is null
        && (!CooldownUntilUtc.HasValue || CooldownUntilUtc <= now);

    public static AgentAvailabilitySnapshot Unknown(
        string workspaceId,
        string agentId,
        DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId,
        AgentId = agentId,
        State = AgentAvailabilityState.Unknown,
        ActivityReason = AgentActivityReason.ConfigurationMissing,
        Version = 0,
        ObservedAtUtc = now,
        ValidUntilUtc = now,
        ReasonCode = "availability_unknown",
    };
}

/// <summary>
/// Persistent projection boundary.  There is intentionally no SetIdle method:
/// callers may only request a rebuild from committed facts.
/// </summary>
public interface IAgentAvailabilityProjectionStore
{
    Task<AgentAvailabilitySnapshot> GetAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default);

    Task<AgentAvailabilitySnapshot> RebuildAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default);
}

