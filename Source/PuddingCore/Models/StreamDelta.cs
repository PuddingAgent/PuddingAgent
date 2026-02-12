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

    /// <summary>Provider SSE data chunk index observed by the gateway.</summary>
    public long? ProviderChunkIndex { get; init; }

    /// <summary>Elapsed time spent awaiting the provider chunk line.</summary>
    public long? ProviderReadMs { get; init; }

    /// <summary>Time gap between this provider data chunk and the previous provider data chunk.</summary>
    public long? ProviderChunkGapMs { get; init; }

    /// <summary>Raw UTF-8 character count of the provider data payload.</summary>
    public int? ProviderPayloadChars { get; init; }

    /// <summary>Time spent parsing the provider data payload into a StreamDelta.</summary>
    public long? GatewayParseMs { get; init; }
}

/// <summary>流式工具调用累积器——将多个 StreamDelta 的工具调用片段拼接为完整调用。</summary>
public sealed class AccumulatedToolCall
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}
