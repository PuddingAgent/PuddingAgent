using PuddingCode.Diagnostics;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 子代理运行详情 DTO — 包含 Manifest + 输出 + 事件/工具计数。
/// 从 SubAgentRunArchive 投影。
/// </summary>
public sealed record SubAgentRunDetailDto
{
    public required SubAgentRunSummaryDto Summary { get; init; }
    public string? Task { get; init; }
    public string? Output { get; init; }
    public Dictionary<string, string> LlmProfiles { get; init; } = new();
    public Dictionary<string, string> Trace { get; init; } = new();
    public int EventCount { get; init; }
    public int ToolCallCount { get; init; }
}

/// <summary>
/// 通用分页结果 DTO。
/// </summary>
public sealed record PagedResultDto<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

/// <summary>
/// 子代理运行事件摘要 DTO — 用于 events 分页列表。
/// 不含完整 payload，只返回 PayloadSize 和 PayloadPreview。
/// </summary>
public sealed record SubAgentRunEventDto
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string Timestamp { get; init; }
    public required int PayloadSize { get; init; }
    public string? PayloadPreview { get; init; }
}
