namespace PuddingCode.Configuration;

public static class LlmProfileResolver
{
    public static ResolvedAgentLlmProfiles Resolve(
        PuddingLlmProvidersConfig llmConfig,
        AgentTemplateManifest? template,
        AgentInstanceLlmConfig? instance)
    {
        return new ResolvedAgentLlmProfiles(
            Conscious: ResolveRole(
                roleName: "conscious",
                llmConfig,
                template?.DefaultLlmProfiles.Conscious,
                instance?.Conscious),
            Subconscious: ResolveRole(
                roleName: "subconscious",
                llmConfig,
                template?.DefaultLlmProfiles.Subconscious,
                instance?.Subconscious));
    }

    private static ResolvedLlmProfile ResolveRole(
        string roleName,
        PuddingLlmProvidersConfig llmConfig,
        string? templateProfileId,
        AgentLlmBinding? binding)
    {
        var roleProfileId = string.Equals(roleName, "conscious", StringComparison.OrdinalIgnoreCase)
            ? llmConfig.Roles.Conscious
            : llmConfig.Roles.Subconscious;

        var profileId = FirstNonBlank(binding?.ProfileId, templateProfileId, roleProfileId);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"No LLM profile configured for role '{roleName}'.");

        if (!llmConfig.Profiles.TryGetValue(profileId, out var profile))
            throw new InvalidOperationException($"LLM profile '{profileId}' for role '{roleName}' was not found.");

        var providerId = FirstNonBlank(binding?.ProviderId, profile.ProviderId)
            ?? throw new InvalidOperationException($"No provider configured for role '{roleName}'.");
        var modelId = FirstNonBlank(binding?.ModelId, profile.ModelId)
            ?? throw new InvalidOperationException($"No model configured for role '{roleName}'.");

        var provider = llmConfig.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new InvalidOperationException($"Provider '{providerId}' for role '{roleName}' was not found.");

        var model = provider.Models.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new InvalidOperationException(
                $"Model binding '{providerId}/{modelId}' for role '{roleName}' does not match a configured provider model.");

        return new ResolvedLlmProfile
        {
            Role = roleName,
            ProfileId = profileId,
            ProviderId = provider.ProviderId,
            ModelId = model.ModelId,
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            ApiKeyRef = provider.ApiKeyRef,
            ReasoningEffort = FirstNonBlank(binding?.ReasoningEffort, profile.ReasoningEffort),
            ThinkingMode = FirstNonBlank(binding?.ThinkingMode, profile.ThinkingMode),
            MaxContextTokens = binding?.MaxContextTokens
                ?? profile.MaxContextTokens
                ?? model.MaxContextTokens,
            MaxReplyTokens = binding?.MaxReplyTokens
                ?? profile.MaxReplyTokens
                ?? model.MaxOutputTokens,
        };
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed record ResolvedAgentLlmProfiles(
    ResolvedLlmProfile Conscious,
    ResolvedLlmProfile Subconscious);

public sealed record ResolvedLlmProfile
{
    public required string Role { get; init; }
    public required string ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiKeyRef { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ThinkingMode { get; init; }
    public int MaxContextTokens { get; init; }
    public int MaxReplyTokens { get; init; }
}
