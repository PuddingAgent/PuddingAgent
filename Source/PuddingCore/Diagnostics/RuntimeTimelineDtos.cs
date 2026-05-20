namespace PuddingCode.Diagnostics;

/// <summary>
/// 运行时 Timeline 查询参数 DTO。
/// 支持按工作区/会话/追踪/Agent实例/Run/组件/状态筛选，支持分页。
/// </summary>
public sealed record RuntimeTimelineQueryDto
{
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? TraceId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? RunId { get; init; }
    public string? Component { get; init; }
    public string? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
    /// <summary>排序方向：asc = 正序（执行顺序），desc = 倒序（最新在前）。默认 desc。</summary>
    public string SortOrder { get; init; } = "desc";
}

/// <summary>
/// 运行时 Timeline 单条记录 DTO — 统一来自 Activity/Event/SessionEvent/SubAgentRun 的投影。
/// Kind 标识来源类型：activity / event / session_frame / subagent_run
/// </summary>
public sealed record RuntimeTimelineItemDto
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? RunId { get; init; }
    public string? EventId { get; init; }
    public string? TraceId { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 组件健康快照 DTO — 各组件最近一段时间的运行统计。
/// Status: healthy / degraded / failing / unknown
/// </summary>
public sealed record RuntimeComponentHealthDto
{
    public required string Component { get; init; }
    public required string Status { get; init; }
    public int StartedCount { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public int RetriedCount { get; init; }
    public int CancelledCount { get; init; }
    public string? LastSeenAtUtc { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// E2E 诊断证据 DTO — 包含一条 TraceId 对应的完整 Timeline + 子代理运行摘要。
/// </summary>
public sealed record DiagnosticEvidenceDto
{
    public required string TraceId { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public required IReadOnlyList<RuntimeTimelineItemDto> Timeline { get; init; }
    public IReadOnlyList<SubAgentRunSummaryDto> SubAgentRuns { get; init; } = Array.Empty<SubAgentRunSummaryDto>();
}

/// <summary>
/// 分页 Timeline 结果 DTO。
/// </summary>
public sealed record PagedTimelineResultDto
{
    public required IReadOnlyList<RuntimeTimelineItemDto> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// 子代理运行摘要 DTO — 用于分页列表和诊断证据。
/// 从 SubAgentRunEntity 投影，不直接暴露 EF Entity。
/// </summary>
public sealed record SubAgentRunSummaryDto
{
    public required string RunId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Status { get; init; }
    public required string StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public long TotalDurationMs { get; init; }
    public int TotalRounds { get; init; }
    public int TotalToolCalls { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 诊断数据脱敏接口 — 对敏感字段（key/token/secret 等）进行脱敏处理。
/// </summary>
public interface IDiagnosticRedactor
{
    /// <summary>对文本进行脱敏：截断超长文本 + 替换敏感关键词。</summary>
    string RedactText(string? value);

    /// <summary>对元数据字典进行脱敏：过滤敏感 key 并脱敏 value。</summary>
    IReadOnlyDictionary<string, string> RedactMetadata(IReadOnlyDictionary<string, string> metadata);
}
