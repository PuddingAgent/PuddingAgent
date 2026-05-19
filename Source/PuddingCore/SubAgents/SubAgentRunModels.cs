namespace PuddingCode.SubAgents;

/// <summary>
/// CompleteRunAsync 幂等写入结果 — 确保 terminal 状态只被写入一次。
/// </summary>
public enum SubAgentRunTerminalWriteResult
{
    /// <summary>terminal 状态已成功写入。</summary>
    Applied,
    /// <summary>run 已是 terminal 状态（completed/failed/cancelled），跳过写入。</summary>
    AlreadyTerminal,
    /// <summary>run 未找到。</summary>
    NotFound
}

// run.json 的 manifest
public sealed record SubAgentRunManifest
{
    public required string RunId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public required string Status { get; init; }  // running, completed, failed, cancelled
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Dictionary<string, string> LlmProfiles { get; init; } = new();
    public Dictionary<string, string> Trace { get; init; } = new();
}

// 创建请求
public sealed record SubAgentRunCreateRequest
{
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
}

// 完成信息
public sealed record SubAgentRunCompletion
{
    public required string Status { get; init; }  // completed, failed, cancelled
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public int TotalRounds { get; init; }
    public int TotalToolCalls { get; init; }
    public long TotalDurationMs { get; init; }
}

// 工具审计条目
public sealed record SubAgentToolAuditEntry
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgsHash { get; init; }
    public required bool Success { get; init; }
    public required long DurationMs { get; init; }
    public int OutputLength { get; init; }
    public string? ErrorMessage { get; init; }
}

// run handle (创建后返回)
public sealed record SubAgentRunHandle
{
    public required string RunId { get; init; }
    public required string ArchivePath { get; init; }
}

// run archive (查询返回)
public sealed record SubAgentRunArchive
{
    public required SubAgentRunManifest Manifest { get; init; }
    public IReadOnlyList<object> Events { get; init; } = Array.Empty<object>();
    public IReadOnlyList<SubAgentToolAuditEntry> Tools { get; init; } = Array.Empty<SubAgentToolAuditEntry>();
    public string? Output { get; init; }
    public string? ErrorOutput { get; init; }
}
