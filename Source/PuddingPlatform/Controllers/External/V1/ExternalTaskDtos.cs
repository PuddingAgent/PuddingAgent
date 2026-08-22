using PuddingCode.Tasks;

namespace PuddingPlatform.Controllers.External.V1;

/// <summary>
/// ADR-075 §8.3: External V1 稳定 wire DTO。与 Internal TaskDtos 分文件分 namespace；
/// V1 只承诺本文件字段，不承诺 EF Entity 或内部枚举数值。
/// </summary>

public sealed class ExternalTaskDto
{
    public string TaskId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<string> AllowedTransitions { get; set; } = [];
    public string BoardColumn { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string ExecutionWindow { get; set; } = string.Empty;
    public string? PreferredAgentId { get; set; }
    public string? ActiveAssignmentId { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset? NextEligibleAtUtc { get; set; }
    public long SortOrder { get; set; }
    public int? ProgressPercent { get; set; }
    public string? ProgressSummary { get; set; }
    public string? BlockerKind { get; set; }
    public string? BlockerReason { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public string? Origin { get; set; }
    public int Version { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? FailedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
}

public sealed class ExternalTaskPageDto
{
    public IReadOnlyList<ExternalTaskDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

public sealed class ExternalCreateTaskRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public string? Priority { get; set; }
    public string? ExecutionWindow { get; set; }
    public string? PreferredAgentId { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public long? SortOrder { get; set; }
}

/// <summary>PATCH 只允许元数据；状态迁移必须走 commands 端点；版本走 If-Match（不收 body expectedVersion）。</summary>
public sealed class ExternalPatchTaskRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public string? Priority { get; set; }
    public string? ExecutionWindow { get; set; }
    public string? PreferredAgentId { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public long? SortOrder { get; set; }
}

public sealed class ExternalTaskCommentDto
{
    public string CommentId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string AuthorKind { get; set; } = string.Empty;
    public string? AuthorId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ExternalCreateCommentRequest
{
    public string Content { get; set; } = string.Empty;
}

public sealed class ExternalTaskEvaluationDto
{
    public string EvaluationId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Comment { get; set; } = string.Empty;
    public int TaskVersionObserved { get; set; }
    public string? SupersedesEvaluationId { get; set; }
    public ExternalEvaluatorDto Evaluator { get; set; } = new();
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ExternalEvaluatorDto
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ExternalCreateEvaluationRequest
{
    /// <summary>accepted | needs_changes | rejected</summary>
    public string Verdict { get; set; } = string.Empty;
    /// <summary>1-5 必填。</summary>
    public int Score { get; set; }
    /// <summary>1-4000 必填。</summary>
    public string Comment { get; set; } = string.Empty;
    /// <summary>必须等于调用前读取的 Task version。</summary>
    public int TaskVersionObserved { get; set; }
    public string? SupersedesEvaluationId { get; set; }
}

/// <summary>commands/{command} 可选 body。</summary>
public sealed class ExternalCommandRequest
{
    public string? AgentId { get; set; }
    public string? WindowDecision { get; set; }
    public string? Reason { get; set; }
}

public sealed class ExternalErrorResponse
{
    public string Code { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? TraceId { get; set; }
    public int? ExpectedVersion { get; set; }
    public int? ActualVersion { get; set; }
}
