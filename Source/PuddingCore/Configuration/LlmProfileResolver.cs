namespace PuddingCode.Configuration;

/// <summary>
/// Agent LLM Profile 解析器 — 按优先级链解析 Conscious/Subconscious 绑定：
///   1. Agent 实例 config/llm.json（最高优先级）
///   2. 模板默认 profiles
///   3. 全局角色默认（roles.conscious / roles.subconscious）
/// 不抛异常：缺失角色返回 null profile。
/// </summary>
public static class LlmProfileResolver
{
    /// <summary>
    /// 解析 Agent 的 LLM profile 绑定。
    /// </summary>
    /// <returns>Conscious 或 Subconscious 为 null 表示该角色未配置。</returns>
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

    /// <summary>解析单个角色的 LLM 绑定，失败返回 null（不抛异常）。</summary>
    private static ResolvedLlmProfile? ResolveRole(
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
            return null;

        if (!llmConfig.Profiles.TryGetValue(profileId, out var profile))
            return null;

        var providerId = FirstNonBlank(binding?.ProviderId, profile.ProviderId);
        if (string.IsNullOrWhiteSpace(providerId))
            return null;

        var modelId = FirstNonBlank(binding?.ModelId, profile.ModelId);
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var provider = llmConfig.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return null;

        var model = provider.Models.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return null;

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
                ?? model.MaxContextTokens
                ?? 0,
            MaxReplyTokens = binding?.MaxReplyTokens
                ?? profile.MaxReplyTokens
                ?? model.MaxOutputTokens
                ?? 0,
        };
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Agent 的 Conscious + Subconscious LLM profile 解析结果。</summary>
public sealed record ResolvedAgentLlmProfiles(
    ResolvedLlmProfile? Conscious,
    ResolvedLlmProfile? Subconscious);

/// <summary>单个角色的完整 LLM 连接信息。</summary>
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
