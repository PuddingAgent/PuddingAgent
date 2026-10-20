namespace PuddingCode.Models;

/// <summary>LLM 响应</summary>
public sealed record LlmResponse(
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls,
    string? ReasoningContent = null,
    TokenUsageDto? Usage = null,
    LlmContinuationState? ContinuationState = null)
{
    /// <summary>Provider terminal reason; partial output must not replace durable context.</summary>
    public string? FinishReason { get; init; }
}
