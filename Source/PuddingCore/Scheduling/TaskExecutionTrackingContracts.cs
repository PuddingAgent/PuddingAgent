using PuddingCode.Goals;
using PuddingCode.Tasks;

namespace PuddingCode.Scheduling;

/// <summary>
/// Read-only health classification for an automatically dispatched Task.
/// A tracking verdict never authorizes a repair or Task state transition.
/// </summary>
public enum TaskExecutionTrackingVerdict
{
    Healthy = 0,
    Waiting = 1,
    Stalled = 2,
    Inconsistent = 3,
    CleanupRequired = 4,
}

public sealed record TaskExecutionTrackingDecision
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public string? AgentId { get; init; }
    public string? AssignmentId { get; init; }
    public string? GoalRunId { get; init; }
    public string? ReservationId { get; init; }
    public string? TaskPlanId { get; init; }
    public string? ExecutionPlanFingerprint { get; init; }
    public string? ExecutionPlanStatus { get; init; }
    public string? WorkUnitKind { get; init; }
    public string? WorkUnitStatus { get; init; }
    public WorkspaceTaskStatus? TaskStatus { get; init; }
    public GoalPhase? GoalPhase { get; init; }
    public string? IterationStatus { get; init; }
    public string? CommandStatus { get; init; }
    public string? RunStatus { get; init; }
    public string? OutboxStatus { get; init; }
    public string? OutboxId { get; init; }
    public required TaskExecutionTrackingVerdict Verdict { get; init; }
    public required string Code { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? LastProgressAtUtc { get; init; }
}

public sealed record TaskExecutionRepairSummary
{
    public required int Examined { get; init; }
    public required int Repaired { get; init; }
    public required IReadOnlyDictionary<string, int> RepairedByCode { get; init; }
}

/// <summary>
/// Authoritative reconciliation writer. It must re-read all fences inside a
/// serializable transaction and only applies an allowlisted deterministic repair.
/// </summary>
public interface ITaskExecutionRepairCoordinator
{
    Task<TaskExecutionRepairSummary> RepairAsync(
        string workspaceId,
        IReadOnlyList<TaskExecutionTrackingDecision> decisions,
        CancellationToken ct = default);
}

public interface ITaskExecutionTracker
{
    Task<IReadOnlyList<TaskExecutionTrackingDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default);
}
