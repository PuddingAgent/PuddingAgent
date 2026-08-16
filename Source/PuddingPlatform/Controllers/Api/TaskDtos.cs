namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// TB-03: WorkspaceTask 查询响应 DTO（wire 值见 TaskWireMaps，不直接暴露 EF Entity）。
/// ARCH-HARDEN-004：所有端点返回专用 DTO。
/// </summary>
public sealed record TaskDto
{
    public required string TaskId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }

    /// <summary>wire: "Backlog"/"Ready"/"Deferred"/"Reserved"/"Assigned"/"NeedsReview"/"InProgress"/"Blocked"/"Completed"/"Failed"/"Cancelled"/"Archived"。</summary>
    public required string Status { get; init; }

    /// <summary>wire: "Backlog"/"Todo"/"InProgress"/"Done"/"Failed"（Cancelled/Archived 无看板列，回退为状态 wire）。</summary>
    public required string BoardColumn { get; init; }

    /// <summary>wire: "p0"/"p1"/"p2"/"p3"。</summary>
    public required string Priority { get; init; }

    /// <summary>wire: "inherit"/"anytime"/"off_peak_only"。</summary>
    public required string ExecutionWindow { get; init; }

    public string? PreferredAgentId { get; init; }
    public string? ActiveAssignmentId { get; init; }
    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
    public long SortOrder { get; init; }
    public int? ProgressPercent { get; init; }
    public string? ProgressSummary { get; init; }
    public string? BlockerKind { get; init; }
    public string? BlockerReason { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public required int Version { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? FailedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
}

/// <summary>创建任务请求 DTO。priority/executionWindow 为 wire 值，缺省时 p3 / inherit。</summary>
public sealed record CreateTaskDto
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public string? Priority { get; init; }
    public string? ExecutionWindow { get; init; }
    public string? PreferredAgentId { get; init; }
    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public long? SortOrder { get; init; }
}

/// <summary>更新任务请求 DTO（CAS，expectedVersion 必填；可空字段 null 表示不更新）。</summary>
public sealed record PatchTaskDto
{
    public required int ExpectedVersion { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public string? Priority { get; init; }
    public string? ExecutionWindow { get; init; }
    public string? PreferredAgentId { get; init; }
    public DateTimeOffset? NotBeforeUtc { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public long? SortOrder { get; init; }
}

/// <summary>指派请求 DTO。</summary>
public sealed record AssignDto
{
    public required string AgentId { get; init; }
    public required int ExpectedVersion { get; init; }
}

/// <summary>立即执行请求 DTO（windowDecision 仅记录，不判定）。</summary>
public sealed record RunNowDto
{
    public required string AgentId { get; init; }
    public required int ExpectedVersion { get; init; }
    public string? WindowDecision { get; init; }
}

/// <summary>cancel/reopen/archive/mark-failed/resume/requeue 通用命令 DTO。</summary>
public sealed record CommandDto
{
    public required int ExpectedVersion { get; init; }
    public string? Reason { get; init; }
}

/// <summary>任务分页响应（keyset）。NextCursor 无更多时为 null。</summary>
public sealed record TaskPageDto
{
    public required IReadOnlyList<TaskDto> Items { get; init; }
    public string? NextCursor { get; init; }
}

/// <summary>稳定错误响应（ST-00.4，用 TB-01 已冻结的 TaskErrorCode 映射）。</summary>
public sealed record TaskErrorResponse
{
    /// <summary>稳定 code，如 "task.version_conflict"。</summary>
    public required string Code { get; init; }

    /// <summary>面向操作者的消息。</summary>
    public required string Message { get; init; }

    public required string TraceId { get; init; }

    /// <summary>CAS 冲突时返回当前 version。</summary>
    public int? Version { get; init; }

    public int? ExpectedVersion { get; init; }
    public int? ActualVersion { get; init; }
}
