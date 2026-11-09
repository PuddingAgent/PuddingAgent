using PuddingExecutionResources.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>Minimal host LLM chat port for review execution (no provider objects or credentials cross this boundary).</summary>
public sealed record RuntimeLlmChatRequest(string WorkspaceId, string SessionId, string AgentTemplateId,
    string ModelId, string ProviderId, string SystemInstruction, string UserInput, int MaxOutputTokens);

public sealed record RuntimeLlmChatResult(string Content, long? InputTokens, long? OutputTokens,
    long? CacheHitTokens, string? FinishReason);

public interface IRuntimeLlmChatClient
{
    ValueTask<RuntimeLlmChatResult> ChatAsync(RuntimeLlmChatRequest request, CancellationToken ct = default);
}

/// <summary>
/// PA-03 provider adapter: resolves the frozen model in the configured LLM catalog and performs one bounded chat call.
/// A model without a configured provider/price authority fails typed before any gateway call.
/// </summary>
public sealed class PlatformReviewModelProvider(IRuntimeLlmChatClient client, Models.ILlmModelCatalogView view)
    : IReviewModelProvider
{
    public async ValueTask<ModelCompletion> CompleteAsync(ReviewModelBinding binding, ModelResourceRequest request,
        CancellationToken ct = default)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.ModelId) || request is null)
            throw new ExecutionResourceException("request.invalid", "A frozen review binding and request are required.");
        var model = view.List().FirstOrDefault(m => string.Equals(m.ModelId, binding.ModelId, StringComparison.Ordinal)
                                                   && !m.IsEmbedding);
        if (model is null || string.IsNullOrWhiteSpace(model.ProviderId))
            throw new ExecutionResourceException("model.configuration_invalid",
                "The frozen review model is not resolvable from the LLM configuration authority.");
        var result = await client.ChatAsync(new RuntimeLlmChatRequest(binding.Identity.WorkspaceId,
            "review:" + request.CallId, "review", model.ModelId, model.ProviderId, request.Instruction, request.Input,
            request.MaxOutputTokens), ct).ConfigureAwait(false);
        var usage = result.InputTokens is { } input && result.OutputTokens is { } output
            ? new ModelUsage(input, output, Math.Clamp(result.CacheHitTokens ?? 0, 0, input))
            : null;
        return new ModelCompletion(result.Content, usage, result.FinishReason);
    }
}
