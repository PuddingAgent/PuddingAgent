namespace PuddingCode.Tasks;

/// <summary>评价结论（ADR-075 §8.7）。评价是追加式反馈事实，不改变 Task 状态。</summary>
public enum TaskEvaluationVerdict
{
    /// <summary>wire: accepted</summary>
    Accepted,

    /// <summary>wire: needs_changes</summary>
    NeedsChanges,

    /// <summary>wire: rejected</summary>
    Rejected,
}

/// <summary>
/// ADR-075: Task 结构化评价（追加式一等子资源）。
/// 约束：score 1-5；comment 必填 1-4000；taskVersionObserved 必须等于评价者读取时的 Task version；
/// 更正用新评价 + SupersedesEvaluationId 指向同一 actor 的旧评价，不 UPDATE/DELETE 历史；
/// 已归档 Task 不接受新评价。
/// </summary>
public sealed record TaskEvaluation
{
    public required string EvaluationId { get; init; }
    public required string TaskId { get; init; }
    public required string WorkspaceId { get; init; }
    public required TaskEvaluationVerdict Verdict { get; init; }
    public required int Score { get; init; }
    public required string Comment { get; init; }
    public required int TaskVersionObserved { get; init; }
    public string? SupersedesEvaluationId { get; init; }
    /// <summary>评价者类型（external_access_token / user / agent）。</summary>
    public required string EvaluatorType { get; init; }
    /// <summary>评价者 ID（external actor 为 access-token:{tokenId}）。</summary>
    public required string EvaluatorId { get; init; }
    public required string EvaluatorDisplayName { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>追加评价命令。</summary>
public sealed record AppendTaskEvaluationRequest
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required TaskEvaluationVerdict Verdict { get; init; }
    public required int Score { get; init; }
    public required string Comment { get; init; }
    public required int TaskVersionObserved { get; init; }
    public string? SupersedesEvaluationId { get; init; }
    public required string EvaluatorType { get; init; }
    public required string EvaluatorId { get; init; }
    public required string EvaluatorDisplayName { get; init; }
}

public enum TaskEvaluationError
{
    None,
    TaskNotFound,
    TaskArchived,
    VersionMismatch,
    InvalidScore,
    InvalidComment,
    InvalidSupersedes,
}
