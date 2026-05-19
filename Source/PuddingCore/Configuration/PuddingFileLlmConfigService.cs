using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingCode.Configuration;

public sealed class PuddingFileLlmConfigService : ILlmConfigService
{
    private readonly Func<PuddingLlmProvidersConfig> _configFactory;
    private PuddingLlmProvidersConfig? _cached;

    public PuddingFileLlmConfigService(PuddingLlmProvidersConfig config)
        : this(() => config)
    {
    }

    public PuddingFileLlmConfigService(Func<PuddingLlmProvidersConfig> configFactory)
    {
        _configFactory = configFactory;
    }

    public IReadOnlyList<LlmProviderInfo> GetEnabledProviders()
    {
        return GetConfig().Providers
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
        return GetConfig().Providers
            .SelectMany(p => p.Models.Select(m => new LlmModelInfo
            {
                ModelId = m.ModelId,
                ProviderId = p.ProviderId,
                Name = m.Name,
                MaxContextTokens = m.MaxContextTokens ?? 0,
                MaxOutputTokens = m.MaxOutputTokens ?? 0,
                IsDefault = m.IsDefault,
                IsDeprecated = m.IsDeprecated,
                SortOrder = m.SortOrder,
            }))
            .ToList();
    }

    public LlmConfig? Resolve(string providerId, string? modelId = null)
    {
        var config = GetConfig();
        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return null;

        var model = ResolveModel(provider, modelId);
        if (model is null)
            return null;

        return ToLlmConfig(provider, model, profile: null);
    }

    public LlmConfig? GetDefault()
    {
        var config = GetConfig();
        return ResolveProfile(config.Roles.Conscious)
            ?? Resolve(config.DefaultProviderId ?? "", config.DefaultModelId);
    }

    public LlmConfig? GetMemoryConfig()
    {
        var config = GetConfig();
        return ResolveProfile(config.Roles.Subconscious)
            ?? GetDefault();
    }

    public LlmProviderStrategy? GetProviderStrategy(string providerId)
    {
        var config = GetConfig();
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
        };
    }

    public void Reload()
    {
        _cached = null;
    }

    private PuddingLlmProvidersConfig GetConfig()
        => _cached ??= _configFactory();

    private LlmConfig? ResolveProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        var config = GetConfig();
        if (!config.Profiles.TryGetValue(profileId, out var profile))
            return null;

        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, profile.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return null;

        var model = ResolveModel(provider, profile.ModelId);
        return model is null ? null : ToLlmConfig(provider, model, profile);
    }

    private static PuddingLlmModelConfig? ResolveModel(PuddingLlmProviderConfig provider, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            return provider.Models.FirstOrDefault(m =>
                !m.IsDeprecated && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        }

        return provider.Models.FirstOrDefault(m => m.IsDefault && !m.IsDeprecated)
            ?? provider.Models.FirstOrDefault(m => !m.IsDeprecated);
    }

    private static LlmConfig ToLlmConfig(
        PuddingLlmProviderConfig provider,
        PuddingLlmModelConfig model,
        PuddingLlmProfileConfig? profile)
    {
#pragma warning disable CS0618
        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            KeyVaultId = NormalizeKeyVaultId(provider.ApiKeyRef),
            ModelId = model.ModelId,
            ReasoningEffort = profile?.ReasoningEffort,
        };
#pragma warning restore CS0618
    }

    private static string? NormalizeKeyVaultId(string? apiKeyRef)
    {
        if (string.IsNullOrWhiteSpace(apiKeyRef))
            return null;

        const string prefix = "vault:";
        return apiKeyRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? apiKeyRef[prefix.Length..]
            : apiKeyRef;
    }
}
