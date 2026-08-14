using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingCode.Runtime;

/// <summary>
/// Resolves the token capacity of a workspace agent's context window.
/// Shared by the context-health HTTP endpoint (PuddingPlatform) and the
/// agent_diagnostics tool (PuddingRuntime) so both read the same
/// provider/model capacity resolution chain instead of re-implementing it.
/// </summary>
public interface IContextCapacityResolver
{
    Task<ResolvedContextCapacity?> ResolveAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default);
}

/// <summary>
/// Resolved context window capacity for a workspace agent.
/// </summary>
public sealed record ResolvedContextCapacity(
    int ContextWindowTokens,
    int? MaxOutputTokens,
    int? MaxInputTokens);

/// <summary>
/// Default resolver: reads the immutable runtime profile via
/// <see cref="IAgentRuntimeProfileResolver"/>, then falls back to the shared
/// <see cref="ILlmConfigService"/> model registry when the profile carries no
/// explicit window size.
/// </summary>
public sealed class ContextCapacityResolver : IContextCapacityResolver
{
    private readonly IAgentRuntimeProfileResolver _profileResolver;
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<ContextCapacityResolver>? _logger;

    public ContextCapacityResolver(
        IAgentRuntimeProfileResolver profileResolver,
        ILlmConfigService llmConfigService,
        ILogger<ContextCapacityResolver>? logger = null)
    {
        _profileResolver = profileResolver;
        _llmConfigService = llmConfigService;
        _logger = logger;
    }

    public async Task<ResolvedContextCapacity?> ResolveAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        AgentRuntimeProfile profile;
        try
        {
            // Context health is a read-only status read. It must reuse the
            // runtime profile boundary first, then ask the LLM configuration
            // service for the provider/model limits. Usage records are evidence
            // of previous calls, not the source of model capacity.
            profile = await _profileResolver.ResolveAsync(workspaceId, agentId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "[ContextCapacity] profile unresolved workspace={WorkspaceId} agent={AgentId}",
                workspaceId,
                agentId);
            return null;
        }

        var providerId = profile.PreferredProviderId;
        var modelId = profile.PreferredModelId ?? profile.LlmConfig?.ModelId;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        if (profile.LlmConfig?.MaxContextTokens is > 0)
        {
            return new ResolvedContextCapacity(
                profile.LlmConfig.MaxContextTokens.Value,
                profile.LlmConfig.MaxOutputTokens is > 0 ? profile.LlmConfig.MaxOutputTokens : null,
                profile.LlmConfig.MaxInputTokens is > 0 ? profile.LlmConfig.MaxInputTokens : null);
        }

        var model = _llmConfigService.GetAllModels().FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        return model?.MaxContextTokens > 0
            ? new ResolvedContextCapacity(
                model.MaxContextTokens,
                model.MaxOutputTokens > 0 ? model.MaxOutputTokens : null,
                model.MaxInputTokens is > 0 ? model.MaxInputTokens : null)
            : null;
    }
}
