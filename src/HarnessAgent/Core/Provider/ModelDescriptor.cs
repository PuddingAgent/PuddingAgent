namespace HarnessAgent.Core.Provider;

/// <summary>Model metadata descriptor.</summary>
public sealed record ModelDescriptor
{
    /// <summary>Unique model id, e.g. "deepseek-v4-pro".</summary>
    public required string ModelId { get; init; }

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Context window size in tokens.</summary>
    public int ContextWindowTokens { get; init; }

    /// <summary>Maximum output tokens.</summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Price per 1M input tokens (USD).</summary>
    public decimal InputPricePerMTokens { get; init; }

    /// <summary>Price per 1M output tokens (USD).</summary>
    public decimal OutputPricePerMTokens { get; init; }

    /// <summary>Capability tags: "fast", "reasoning", "vision", "coding", etc.</summary>
    public IReadOnlySet<string> CapabilityTags { get; init; } = new HashSet<string>();

    /// <summary>Whether this model supports function/tool calling.</summary>
    public bool SupportsToolCalling { get; init; }

    /// <summary>Whether this model is deprecated.</summary>
    public bool IsDeprecated { get; init; }
}
