using PuddingCode.Abstractions;

namespace PuddingCode.Platform;

/// <summary>
/// 潜意识整合任务。
/// </summary>
public record ConsolidationJob
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentTemplateId { get; init; }
    /// <summary>用户刚发送的消息文本（可选，避免 SessionId 跨系统映射问题）。</summary>
    public string? LastUserMessage { get; init; }
    /// <summary>Agent 的回复文本（可选，同上）。</summary>
    public string? LastAssistantReply { get; init; }
}

/// <summary>
/// 会话结构化摘要。
/// </summary>
public record SessionSummary
{
    public required string SessionId { get; init; }
    public string? Title { get; init; }
    public List<ExtractedFact> Facts { get; init; } = [];
    public List<ExtractedPreference> Preferences { get; init; } = [];
    public string? OneLineSummary { get; init; }
    public List<string> SuggestedTags { get; init; } = [];
}

/// <summary>
/// 抽取出的事实项。
/// </summary>
public record ExtractedFact
{
    public required string Statement { get; init; }
    public double Confidence { get; init; } = 0.8;
    public string? SourceMessageId { get; init; }
}

/// <summary>
/// 抽取出的偏好项。
/// </summary>
public record ExtractedPreference
{
    public required string Category { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? SourceMessageId { get; init; }
}

/// <summary>
/// 记忆仪表盘摘要。
/// </summary>
public record MemoryDashboard
{
    public int TotalBooks { get; init; }
    public int TotalChapters { get; init; }
    public int TotalFacts { get; init; }
    public int TotalPointers { get; init; }
    public DateTimeOffset? LastConsolidationAt { get; init; }
    public List<TagTreeNode> TopTags { get; init; } = [];
}

/// <summary>
/// 记忆搜索请求。
/// </summary>
public record MemorySearchRequest
{
    public string? WorkspaceId { get; init; }
    public string? Query { get; init; }
    public string? TagFilter { get; init; }
    public string? SortBy { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// <summary>
/// 记忆搜索结果。
/// </summary>
public record MemorySearchResult
{
    public List<MemoryEntryDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
}

/// <summary>
/// 记忆条目 DTO。
/// </summary>
public record MemoryEntryDto
{
    public required string EntryId { get; init; }
    public required string EntryType { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public double Importance { get; init; }
    public string? SourceSessionId { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// 记忆专用 LLM 配置。
/// </summary>
public record MemoryLlmConfig(
    string? Endpoint,
    string? ApiKey,
    string? ModelId);
