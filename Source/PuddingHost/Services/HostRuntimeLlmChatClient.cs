using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Services.Models;
using PuddingPlatform.Services.Reviews;
using PuddingRuntime.Services;

namespace PuddingHost.Services;

/// <summary>
/// PA-03 host gateway adapter: resolves the frozen model/provider in the existing LLM configuration authority and calls
/// the shared runtime client. Credentials remain inside the host; the platform provider only sees usage/content.
/// </summary>
public sealed class HostRuntimeLlmChatClient(IRuntimeLlmClient client, ILlmConfigService configs) : IRuntimeLlmChatClient
{
    public async ValueTask<RuntimeLlmChatResult> ChatAsync(RuntimeLlmChatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = configs.Resolve(request.ProviderId, request.ModelId);
        if (resolved is null)
            throw new PuddingExecutionResources.Contracts.ExecutionResourceException("model.configuration_invalid",
                "The frozen review model is not resolvable from the LLM configuration authority.");
        // The bounded review request caps output tokens for this attempt only.
        var config = resolved with { MaxOutputTokens = request.MaxOutputTokens };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, request.SystemInstruction),
            new(ChatRole.User, request.UserInput),
        };
        var response = await client.ChatAsync(request.WorkspaceId, request.SessionId, request.AgentTemplateId, messages,
            tools: null, config, ct).ConfigureAwait(false);
        return new RuntimeLlmChatResult(response.Content, response.Usage?.PromptTokens, response.Usage?.CompletionTokens,
            response.Usage?.PromptCacheHitTokens, response.FinishReason);
    }
}
