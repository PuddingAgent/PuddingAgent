namespace PuddingCode.Models;

/// <summary>LLM 响应</summary>
public sealed record LlmResponse(
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls,
    string? ReasoningContent = null,
    TokenUsageDto? Usage = null);
