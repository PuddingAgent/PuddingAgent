namespace HarnessAgent.Core.Provider;

/// <summary>Chat message role.</summary>
public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A single chat message.</summary>
public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };
    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };
    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = ChatRole.Assistant, Content = content, ToolCalls = toolCalls };
    public static ChatMessage Tool(string content, string toolCallId) =>
        new() { Role = ChatRole.Tool, Content = content, ToolCallId = toolCallId };
}

/// <summary>Tool call within an assistant message.</summary>
public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; } // JSON string
}

/// <summary>Chat completion request.</summary>
public sealed record ChatCompletionRequest
{
    public required string ModelId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public float Temperature { get; init; } = 0.7f;
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public string? ToolChoice { get; init; } // "auto", "none", or specific tool name
}

/// <summary>Tool definition for function calling.</summary>
public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required object Parameters { get; init; } // JSON Schema object
}

/// <summary>Chat completion response.</summary>
public sealed record ChatCompletionResponse
{
    public required string ModelId { get; init; }
    public required ChatMessage Message { get; init; }
    public required TokenUsage Usage { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>A single streaming chunk.</summary>
public sealed record ChatCompletionChunk
{
    public string? ContentDelta { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>Token usage statistics.</summary>
public sealed record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public int? CachedInputTokens { get; init; }
}
