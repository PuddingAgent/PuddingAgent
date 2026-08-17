using System.Text.Json;
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
/// 包含完整 payload，并保留 PayloadSize 和 PayloadPreview 供轻量列表使用。
/// </summary>
public sealed record SubAgentRunEventDto
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string Timestamp { get; init; }
    public required int PayloadSize { get; init; }
    public string? PayloadPreview { get; init; }
    /// <summary>
    /// 认证后的运行检查器使用的完整归档 payload。运行事件本身就是可审计事实，
    /// 不能只返回 200 字符预览，否则历史回放无法重建推理、工具输入和工具输出。
    /// </summary>
    public JsonElement? Payload { get; init; }
}
