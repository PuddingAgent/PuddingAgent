namespace PuddingCode.Scheduling;

/// <summary>
/// Durable logical work slot for one automatically dispatched task.  Runtime
/// execution leases may come and go while this reservation remains active.
/// </summary>
public sealed record AgentExecutionReservationSnapshot
{
    public required string ReservationId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string TaskId { get; init; }
    public string? GoalRunId { get; init; }
    public required long FencingToken { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset LeaseUntilUtc { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ReleasedAtUtc { get; init; }
}

public enum AgentReservationResultKind
{
    Acquired,
    AlreadyOwned,
    Conflict,
}

public sealed record AgentReservationResult(
    AgentReservationResultKind Kind,
    AgentExecutionReservationSnapshot Reservation);

public interface IAgentExecutionReservationStore
{
    Task<AgentReservationResult> TryReserveAsync(
        string workspaceId,
        string agentId,
        string taskId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> RenewAsync(
        string reservationId,
        long fencingToken,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> ReleaseAsync(
        string reservationId,
        long fencingToken,
        string ownerId,
        string reason,
        CancellationToken ct = default);

    Task<int> ExpireAsync(CancellationToken ct = default);
}

