using PuddingCode.Tasks;

namespace PuddingCode.Scheduling;

public enum TaskAutoDispatchCandidateVerdict
{
    Eligible = 0,
    Deferred = 1,
    Denied = 2,
}

/// <summary>Evaluate-only scheduling result. It never implies that a Task was dispatched.</summary>
public sealed record TaskAutoDispatchCandidateDecision
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public int? TaskVersion { get; init; }
    public string? AgentId { get; init; }
    public string? TaskType { get; init; }
    public string? AgentSelectionCode { get; init; }
    public string? AgentRoutingFingerprint { get; init; }
    public string? ExecutionPlanFingerprint { get; init; }
    public int? ExecutionPlanSchemaVersion { get; init; }
    public int? ExecutionPlanVersion { get; init; }
    public string? ConversationId { get; init; }
    public TaskExecutionWindow? ExecutionWindow { get; init; }
    public required TaskAutoDispatchCandidateVerdict Verdict { get; init; }
    public required string Code { get; init; }
    public required DateTimeOffset EvaluatedAtUtc { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
    public long? AvailabilityVersion { get; init; }
    public string? AvailabilityReason { get; init; }
    public string? DependencyState { get; init; }
    public string? WindowCode { get; init; }
}

public interface ITaskAutoDispatchEvaluator
{
    Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default);
}
