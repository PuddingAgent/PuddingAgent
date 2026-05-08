namespace PuddingCode.Models;

/// <summary>A single SSE streaming delta from the LLM.</summary>
public sealed record StreamDelta
{
    /// <summary>Content text delta (assistant reply token).</summary>
    public string? ContentDelta { get; init; }

    /// <summary>Reasoning / thinking text delta (DeepSeek Reasoner, o1, etc.).</summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>Tool call accumulation: tool call index.</summary>
    public int? ToolCallIndex { get; init; }

    /// <summary>Tool call id (first chunk only).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool call function name delta.</summary>
    public string? ToolCallNameDelta { get; init; }

    /// <summary>Tool call function arguments delta.</summary>
    public string? ToolCallArgsDelta { get; init; }

    /// <summary>Finish reason: "stop", "tool_calls", etc. null if not finished.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Token usage payload, usually emitted by the final streaming chunk.</summary>
    public TokenUsageDto? Usage { get; init; }
}

/// <summary>流式工具调用累积器——将多个 StreamDelta 的工具调用片段拼接为完整调用。</summary>
public sealed class AccumulatedToolCall
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}
