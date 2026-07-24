namespace PuddingCode.SubAgents;

/// <summary>
/// 子代理诊断请求参数。
/// </summary>
public sealed record SubAgentDiagnosticsRequest
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }

    /// <summary>回溯时间窗口（小时），默认 24。</summary>
    public int HoursBack { get; init; } = 24;

    /// <summary>最大扫描 run 数量，默认 200。</summary>
    public int MaxRuns { get; init; } = 200;
}

/// <summary>
/// 单次子代理运行的摘要信息。
/// </summary>
public sealed record SubAgentRunSummary
{
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? Role { get; init; }
    public string? OriginToolId { get; init; }
    public string? ModelId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>运行时长（毫秒）。未完成时按当前时间计算。</summary>
    public long DurationMs { get; init; }

    public int TotalRounds { get; init; }
    public int TotalToolCalls { get; init; }

    /// <summary>错误信息（截断至 120 字符）。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 按角色聚合的子代理运行统计。
/// </summary>
public sealed record SubAgentRoleStats
{
    public required string Role { get; init; }
    public int TotalRuns { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int CancelledCount { get; init; }
    public int TimeoutCount { get; init; }

    /// <summary>失败原因分类：ErrorMessage 包含 timeout/canceled/cancelled。</summary>
    public int FailureTimeoutCount { get; init; }
    /// <summary>失败原因分类：ErrorMessage 包含 tool/Tool。</summary>
    public int ToolErrorCount { get; init; }
    /// <summary>失败原因分类：ErrorMessage 包含 llm/LLM/api/API。</summary>
    public int LlmErrorCount { get; init; }
    /// <summary>失败原因分类：ErrorMessage 包含 guard/block/safety。</summary>
    public int GuardrailBlockedCount { get; init; }
    /// <summary>失败原因分类：不匹配以上任何类别。</summary>
    public int UnknownErrorCount { get; init; }

    public double AvgDurationMs { get; init; }
    public double P50DurationMs { get; init; }
    public double P95DurationMs { get; init; }
    public double AvgRounds { get; init; }
    public double AvgToolCalls { get; init; }
}

/// <summary>
/// 按模型聚合的子代理运行统计。
/// </summary>
public sealed record SubAgentModelStats
{
    public required string ModelId { get; init; }
    public required SubAgentRoleStats Stats { get; init; }
}

/// <summary>
/// 子代理运行时诊断报告。
/// </summary>
public sealed record SubAgentDiagnosticsReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public required SubAgentDiagnosticsRequest Request { get; init; }

    /// <summary>全局聚合统计（所有角色合并）。</summary>
    public required SubAgentRoleStats Overall { get; init; }

    /// <summary>按角色分组的统计。</summary>
    public IReadOnlyList<SubAgentRoleStats> ByRole { get; init; } = [];

    /// <summary>按模型分组的统计。</summary>
    public IReadOnlyList<SubAgentModelStats> ByModel { get; init; } = [];

    /// <summary>最近的运行摘要列表。</summary>
    public IReadOnlyList<SubAgentRunSummary> RecentRuns { get; init; } = [];
}

/// <summary>
/// 子代理单次运行的延迟分解（从 events.jsonl 计算）。
/// </summary>
public sealed record SubAgentLatencyBreakdown
{
    public required string RunId { get; init; }
    public long TotalDurationMs { get; init; }
    public long LlmDurationMs { get; init; }
    public long ToolDurationMs { get; init; }
    public long OverheadMs { get; init; }
    public int RoundCount { get; init; }
    public int ToolCallCount { get; init; }
    public double LlmPct { get; init; }
    public double ToolPct { get; init; }
    public double OverheadPct { get; init; }
}
