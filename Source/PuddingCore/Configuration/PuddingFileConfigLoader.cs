using System.Text.Json;

namespace PuddingCode.Configuration;

public sealed class PuddingFileConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly PuddingDataPaths _paths;

    public PuddingFileConfigLoader(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<PuddingLlmProvidersConfig> LoadLlmProvidersAsync(CancellationToken ct = default)
    {
        var config = await LoadJsonAsync<PuddingLlmProvidersConfig>(
            _paths.SystemConfigFile("llm.providers.json"),
            ct);

        ValidateLlmProviders(config);
        return config;
    }

    public async Task<T> LoadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}", path);

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        return config ?? throw new InvalidOperationException($"Configuration file is empty or invalid: {path}");
    }

    private static void ValidateLlmProviders(PuddingLlmProvidersConfig config)
    {
        if (config.Providers.Count == 0)
            throw new InvalidOperationException("llm.providers.json must define at least one provider.");

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new InvalidOperationException("llm.providers.json contains a provider with empty providerId.");
            if (!providerIds.Add(provider.ProviderId))
                throw new InvalidOperationException($"llm.providers.json contains duplicate providerId '{provider.ProviderId}'.");
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                throw new InvalidOperationException($"llm.providers.json provider '{provider.ProviderId}' has empty baseUrl.");
            if (provider.Models.Count == 0)
                throw new InvalidOperationException($"llm.providers.json provider '{provider.ProviderId}' must define at least one model.");

            var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId))
                    throw new InvalidOperationException($"llm.providers.json provider '{provider.ProviderId}' contains a model with empty modelId.");
                if (!modelIds.Add(model.ModelId))
                    throw new InvalidOperationException($"llm.providers.json provider '{provider.ProviderId}' contains duplicate modelId '{model.ModelId}'.");
            }
        }

        if (config.Profiles.Count == 0)
            throw new InvalidOperationException("llm.providers.json must define at least one profile.");

        foreach (var (profileId, profile) in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new InvalidOperationException("llm.providers.json contains an empty profile id.");
            if (string.IsNullOrWhiteSpace(profile.ProviderId))
                throw new InvalidOperationException($"llm.providers.json profile '{profileId}' has empty providerId.");
            if (string.IsNullOrWhiteSpace(profile.ModelId))
                throw new InvalidOperationException($"llm.providers.json profile '{profileId}' has empty modelId.");
            if (!ProviderHasModel(config, profile.ProviderId, profile.ModelId))
                throw new InvalidOperationException(
                    $"llm.providers.json profile '{profileId}' references missing provider/model '{profile.ProviderId}/{profile.ModelId}'.");
        }

        ValidateRole(config, "roles.conscious", config.Roles.Conscious);
        ValidateRole(config, "roles.subconscious", config.Roles.Subconscious);
    }

    private static void ValidateRole(PuddingLlmProvidersConfig config, string roleName, string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"llm.providers.json {roleName} must reference a profile.");
        if (!config.Profiles.ContainsKey(profileId))
            throw new InvalidOperationException($"llm.providers.json {roleName} references missing profile '{profileId}'.");
    }

    private static bool ProviderHasModel(PuddingLlmProvidersConfig config, string providerId, string modelId)
    {
        var provider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider?.Models.Any(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
