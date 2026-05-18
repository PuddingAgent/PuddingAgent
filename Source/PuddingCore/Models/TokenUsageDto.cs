namespace PuddingCode.Models;

/// <summary>
/// LLM token usage reported by OpenAI-compatible providers.
/// Prompt/completion/total values come from the provider's usage payload;
/// context window is optional metadata filled by upper layers when known.
/// </summary>
public sealed record TokenUsageDto
{
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int? ContextWindowTokens { get; init; }

    /// <summary>DeepSeek/OpenAI 兼容：prompt 缓存命中 tokens 数</summary>
    public int? PromptCacheHitTokens { get; init; }

    /// <summary>DeepSeek/OpenAI 兼容：prompt 缓存未命中 tokens 数</summary>
    public int? PromptCacheMissTokens { get; init; }
}
