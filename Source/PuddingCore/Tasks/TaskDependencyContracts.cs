namespace PuddingCode.Tasks;

public enum TaskDependencyEvaluationState
{
    Satisfied,
    Waiting,
    Broken,
}

public sealed record TaskDependency
{
    public required string DependencyId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string PredecessorTaskId { get; init; }
    public required string SuccessorTaskId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record TaskDependencyEvaluation
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required TaskDependencyEvaluationState State { get; init; }
    public required IReadOnlyList<string> WaitingOnTaskIds { get; init; }
    public required IReadOnlyList<string> BrokenByTaskIds { get; init; }
    public string ReasonCode => State switch
    {
        TaskDependencyEvaluationState.Satisfied => "dependencies_satisfied",
        TaskDependencyEvaluationState.Waiting => "waiting_dependency",
        TaskDependencyEvaluationState.Broken => "dependency_terminal_without_completion",
        _ => "dependency_unknown",
    };
}

public interface ITaskDependencyStore
{
    Task<TaskDependency> AddAsync(
        string workspaceId,
        string predecessorTaskId,
        string successorTaskId,
        CancellationToken ct = default);

    Task<bool> RemoveAsync(
        string workspaceId,
        string dependencyId,
        CancellationToken ct = default);

    Task<IReadOnlyList<TaskDependency>> ListAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default);

    Task<TaskDependencyEvaluation> EvaluateAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default);
}

