namespace HarnessAgent.Core.Provider;

/// <summary>
/// Unified LLM provider interface — models Pi Agent's provider pattern.
/// Providers register models and handle chat completions.
/// </summary>
public interface IModelProvider
{
    /// <summary>Provider id, e.g. "deepseek", "openai", "anthropic".</summary>
    string ProviderId { get; }

    /// <summary>Models registered with this provider.</summary>
    IReadOnlyList<ModelDescriptor> Models { get; }

    /// <summary>Send a chat completion request to this provider.</summary>
    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken ct = default);

    /// <summary>Stream a chat completion, yielding each chunk.</summary>
    IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken ct = default);
}
