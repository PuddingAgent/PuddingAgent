using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingCode.Configuration;

/// <summary>
/// LLM 配置服务 — 从 data/config/llm.providers.json 加载一次，不热重载。
/// 所有 Agent LLM 调用均通过此服务获取配置。
/// </summary>
public sealed class PuddingFileLlmConfigService : ILlmConfigService
{
    private readonly object _sync = new();
    private PuddingLlmProvidersConfig _config;

    public PuddingFileLlmConfigService(PuddingLlmProvidersConfig config)
    {
        _config = config;
    }

    /// <summary>用文件服务刚写入的配置刷新运行时快照。</summary>
    public void Reload(PuddingLlmProvidersConfig config)
    {
        lock (_sync)
        {
            _config = config;
        }
    }

    void ILlmConfigService.Reload(object config)
    {
        if (config is PuddingLlmProvidersConfig typed)
            Reload(typed);
    }

    public IReadOnlyList<LlmProviderInfo> GetEnabledProviders()
    {
        var config = Snapshot();
        return config.Providers
            .Where(p => p.IsEnabled)
            .Select(p => new LlmProviderInfo
            {
                ProviderId = p.ProviderId,
                Name = p.Name,
                Protocol = p.Protocol,
                BaseUrl = p.BaseUrl,
                IsEnabled = p.IsEnabled,
                HasApiKey = !string.IsNullOrWhiteSpace(p.ApiKey)
                    || !string.IsNullOrWhiteSpace(p.ApiKeyRef),
            })
            .ToList();
    }

    public IReadOnlyList<LlmModelInfo> GetAllModels()
    {
        var config = Snapshot();
        return config.Providers
            .SelectMany(p => p.Models.Select(m => new LlmModelInfo
            {
                ModelId = m.ModelId,
                ProviderId = p.ProviderId,
                Name = m.Name,
                MaxContextTokens = m.MaxContextTokens ?? 0,
                MaxOutputTokens = m.MaxOutputTokens ?? 0,
                InputPricePer1MTokens = m.PricePer1MInputTokens ?? 0,
                OutputPricePer1MTokens = m.PricePer1MOutputTokens ?? 0,
                CacheHitPricePer1MTokens = m.PricePer1MCacheHitTokens ?? m.PricePer1MInputTokens ?? 0,
                IsDefault = m.IsDefault,
                                IsDeprecated = m.IsDeprecated,
                IsEmbedding = m.IsEmbedding,
                SortOrder = m.SortOrder,
                CapabilityTags = m.CapabilityTags ?? [],
            }))
            .ToList();
    }

    public LlmConfig? Resolve(string providerId, string? modelId = null)
    {
        var config = Snapshot();
        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;

        var model = ResolveModel(provider, modelId);
        return model is null ? null : ToLlmConfig(provider, model, profile: null);
    }

    public LlmProfileInfo? ResolveProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;

        var config = Snapshot();
        if (!config.Profiles.TryGetValue(profileId, out var profile)) return null;

        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, profile.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;

        var model = ResolveModel(provider, profile.ModelId);
        if (model is null) return null;

        return new LlmProfileInfo
        {
            ProfileId = profileId,
            ProviderId = provider.ProviderId,
            ModelId = model.ModelId,
            Config = ToLlmConfig(provider, model, profile),
        };
    }

    public LlmProfileInfo GetDefaultProfile()
    {
        var config = Snapshot();
        if (!string.IsNullOrWhiteSpace(config.Roles.Conscious))
        {
            var conscious = ResolveProfile(config.Roles.Conscious);
            if (conscious is not null)
                return conscious;
        }

        var providerId = config.DefaultProviderId;
        var provider = config.Providers.FirstOrDefault(candidate =>
            candidate.IsEnabled
            && string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        var model = provider is null ? null : ResolveModel(provider, config.DefaultModelId);
        if (provider is null || model is null)
        {
            throw new InvalidOperationException(
                "LLM provider 'profiles.conscious' is not configured or its provider/model is not found " +
                "in data/config/llm.providers.json. Ensure profiles.conscious.providerId and profiles.conscious.modelId " +
                "match an enabled provider.");
        }

        return new LlmProfileInfo
        {
            ProfileId = "default-conscious",
            ProviderId = provider.ProviderId,
            ModelId = model.ModelId,
            Config = ToLlmConfig(provider, model, profile: null),
        };
    }

    public LlmConfig GetDefault() => GetDefaultProfile().Config;

    public LlmConfig? GetMemoryConfig()
    {
        var config = Snapshot();
        return ResolveRoleProfileConfig(config, config.Roles.Subconscious);
    }

    public LlmConfig? GetEmbeddingConfig()
    {
        var config = Snapshot();

        // ① 确定 provider：Embedding 节默认 > 扫描 isEmbedding 模型 > Roles.Subconscious > 第一个 enabled
        var resolvedProviderId = config.Embedding?.ProviderId
            ?? config.Providers.FirstOrDefault(p =>
                p.IsEnabled && p.Models.Any(m => !m.IsDeprecated && m.IsEmbedding))?.ProviderId
            ?? config.Roles.Subconscious
            ?? config.DefaultProviderId;

        if (string.IsNullOrWhiteSpace(resolvedProviderId)) return null;

        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, resolvedProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;

        // ② 确定 model：Embedding 节默认 > provider 下第一个 IsEmbedding 模型
        var resolvedModelId = config.Embedding?.ModelId;
        PuddingLlmModelConfig? model = null;

        if (!string.IsNullOrWhiteSpace(resolvedModelId))
        {
            model = provider.Models.FirstOrDefault(m =>
                !m.IsDeprecated && string.Equals(m.ModelId, resolvedModelId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            model = provider.Models.FirstOrDefault(m => !m.IsDeprecated && m.IsEmbedding);
        }

        if (model is null) return null;

        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            KeyVaultId = NormalizeKeyVaultId(provider.ApiKeyRef),
            ModelId = model.ModelId,
            MaxContextTokens = model.MaxContextTokens,
            MaxOutputTokens = model.MaxOutputTokens,
        };
    }

    public LlmProviderStrategy? GetProviderStrategy(string providerId)
    {
        var config = Snapshot();
        var provider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;

        return new LlmProviderStrategy
        {
            RequestTimeoutSeconds = provider.RequestTimeoutSeconds,
            StreamTimeoutSeconds = provider.StreamTimeoutSeconds,
            MaxRetries = provider.MaxRetries,
            RetryDelaySeconds = provider.RetryDelaySeconds,
            CircuitBreakerFailureThreshold = provider.CircuitBreakerFailureThreshold,
            CircuitBreakerRecoverySeconds = provider.CircuitBreakerRecoverySeconds,
            MaxConcurrentRequests = provider.MaxConcurrentRequests,
            TokensPerMinute = provider.TokensPerMinute,
            RequestsPerMinute = provider.RequestsPerMinute,
            Compat = MapCompat(provider.Compat),
        };
    }

    private static ProviderCompatConfig? MapCompat(PuddingProviderCompatConfig? src)
    {
        if (src is null) return null;
        return new ProviderCompatConfig
        {
            MaxTokensField = src.MaxTokensField,
            RequiresStringContent = src.RequiresStringContent,
            UseReasoningEffort = src.UseReasoningEffort,
            DefaultReasoningEffort = src.DefaultReasoningEffort,
            SupportsUsageInStreaming = src.SupportsUsageInStreaming,
            RequiresReasoningContentInToolMessages = src.RequiresReasoningContentInToolMessages,
        };
    }

    public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId)
    {
        var config = Snapshot();
        var provider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        var model = provider?.Models.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        if (provider is null && model?.MaxConcurrentRequests is null) return null;

        return new LlmProviderStrategy
        {
            RequestTimeoutSeconds = provider?.RequestTimeoutSeconds,
            StreamTimeoutSeconds = provider?.StreamTimeoutSeconds,
            MaxRetries = provider?.MaxRetries,
            RetryDelaySeconds = provider?.RetryDelaySeconds,
            CircuitBreakerFailureThreshold = provider?.CircuitBreakerFailureThreshold,
            CircuitBreakerRecoverySeconds = provider?.CircuitBreakerRecoverySeconds,
            MaxConcurrentRequests = model?.MaxConcurrentRequests ?? provider?.MaxConcurrentRequests,
            TokensPerMinute = provider?.TokensPerMinute,
            RequestsPerMinute = provider?.RequestsPerMinute,
            Compat = MapCompat(provider?.Compat),
        };
    }

    /// <summary>获取文件式 TTS/ASR 语音配置快照。</summary>
    public PuddingVoiceProvidersConfig? GetVoiceConfig() => Snapshot().Voice;

    private PuddingLlmProvidersConfig Snapshot()
    {
        lock (_sync)
        {
            return _config;
        }
    }

    private static LlmConfig? ResolveRoleProfileConfig(PuddingLlmProvidersConfig config, string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        if (!config.Profiles.TryGetValue(profileId, out var profile)) return null;
        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, profile.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;
        var model = ResolveModel(provider, profile.ModelId);
        return model is null ? null : ToLlmConfig(provider, model, profile);
    }

    private static PuddingLlmModelConfig? ResolveModel(PuddingLlmProviderConfig provider, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
            return provider.Models.FirstOrDefault(m =>
                !m.IsDeprecated && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        return provider.Models.FirstOrDefault(m => m.IsDefault && !m.IsDeprecated)
            ?? provider.Models.FirstOrDefault(m => !m.IsDeprecated);
    }

    private static LlmConfig ToLlmConfig(
        PuddingLlmProviderConfig provider, PuddingLlmModelConfig model, PuddingLlmProfileConfig? profile)
    {
        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            KeyVaultId = NormalizeKeyVaultId(provider.ApiKeyRef),
            ModelId = model.ModelId,
            MaxContextTokens = model.MaxContextTokens,
            MaxOutputTokens = model.MaxOutputTokens,
            ReasoningEffort = profile?.ReasoningEffort,
        };
    }

    private static string? NormalizeKeyVaultId(string? apiKeyRef)
    {
        if (string.IsNullOrWhiteSpace(apiKeyRef)) return null;
        const string prefix = "vault:";
        return apiKeyRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? apiKeyRef[prefix.Length..] : apiKeyRef;
    }
}
