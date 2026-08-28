namespace PuddingCode.Scheduling;

public enum TaskBacklogRefinementVerdict
{
    ReadyCandidate = 0,
    NeedsRefinement = 1,
}

/// <summary>Backlog 自动准入的只读判定；不表示状态已经变为 Ready。</summary>
public sealed record TaskBacklogRefinementDecision
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required int TaskVersion { get; init; }
    public required string TaskType { get; init; }
    public required TaskBacklogRefinementVerdict Verdict { get; init; }
    public required string Code { get; init; }
    public string? CompatibleAgentId { get; init; }
    public string? AgentRoutingFingerprint { get; init; }
}

public interface ITaskBacklogRefinementEvaluator
{
    Task<IReadOnlyList<TaskBacklogRefinementDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default);
}

public static class TaskBacklogPromotionCodes
{
    public const string Promoted = "backlog_promoted_to_ready";
    public const string TaskChanged = "backlog_task_changed";
    public const string RouteChanged = "backlog_route_changed";
    public const string NotReady = "backlog_not_ready";
}

public sealed record PromoteBacklogTaskCommand
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required int ExpectedTaskVersion { get; init; }
    public required string CompatibleAgentId { get; init; }
    public required string ExpectedAgentRoutingFingerprint { get; init; }
}

public sealed record PromoteBacklogTaskResult(bool Promoted, string Code, int? TaskVersion = null);

public interface ITaskBacklogRefinementStore
{
    Task<PromoteBacklogTaskResult> TryPromoteAsync(
        PromoteBacklogTaskCommand command,
        CancellationToken ct = default);
}
