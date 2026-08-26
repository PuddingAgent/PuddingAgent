using PuddingCode.Scheduling;
using PuddingCode.Tasks;

namespace PuddingCode.Goals;

public static class TaskBoundGoalStartCodes
{
    public const string Started = "task_goal_started";
    public const string IdempotentReplay = "task_goal_idempotent_replay";
    public const string TaskMissing = "task_missing";
    public const string TaskChanged = "task_changed";
    public const string TaskNotEligible = "task_not_eligible";
    public const string AgentChanged = "agent_changed";
    public const string AgentNotIdle = "agent_not_idle";
    public const string ConversationMissing = "agent_conversation_missing";
    public const string DependencyChanged = "task_dependency_changed";
    public const string WindowChanged = "execution_window_changed";
    public const string WindowExpired = "execution_window_expired";
    public const string LostRace = "task_goal_lost_race";
    public const string PrerequisiteDisabled = "task_goal_prerequisite_disabled";
}

/// <summary>
/// A fully evaluated automatic-start request. Task text remains untrusted data;
/// only the coordinator may construct this command from canonical projections.
/// </summary>
public sealed record StartGoalFromTaskCommand
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required int ExpectedTaskVersion { get; init; }
    public required string AgentId { get; init; }
    public required string ConversationId { get; init; }
    public required long ExpectedAvailabilityVersion { get; init; }
    public required TaskExecutionWindow ExecutionWindow { get; init; }
    public required ExecutionWindowDecision WindowDecision { get; init; }
    public required int GoalIterationBudget { get; init; }
    public required TimeSpan MinimumIdle { get; init; }
    public required TimeSpan ReservationLease { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
    public required string OwnerId { get; init; }
    public required string CausationId { get; init; }
    public required string CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed record TaskBoundGoalStartResult
{
    public required bool Started { get; init; }
    public required string Code { get; init; }
    public string? GoalRunId { get; init; }
    public string? AssignmentId { get; init; }
    public string? ReservationId { get; init; }
    public long? ReservationFencingToken { get; init; }
    public int? TaskVersion { get; init; }
}

public interface ITaskGoalDispatchTransactionStore
{
    Task<TaskBoundGoalStartResult> StartAsync(
        StartGoalFromTaskCommand command,
        CancellationToken ct = default);
}

