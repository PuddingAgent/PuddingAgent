using System.Text.Json;

namespace PuddingCode.Configuration;

/// <summary>
/// 文件配置加载器 — 从 data/config/*.json 加载类型化配置，验证后返回结构化结果。
/// 验证错误不抛异常，由调用方通过 ConfigLoadResult 处理。
/// </summary>
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

    /// <summary>加载并验证 LLM 服务商/模型/Profile 配置。</summary>
    public async Task<ConfigLoadResult<PuddingLlmProvidersConfig>> LoadLlmProvidersAsync(CancellationToken ct = default)
    {
        var loadResult = await LoadJsonAsync<PuddingLlmProvidersConfig>(
            _paths.SystemConfigFile("llm.providers.json"),
            ct);

        if (!loadResult.Success)
            return loadResult;

        var config = loadResult.Config!;
        var errors = ValidateLlmProviders(config);
        return errors.Count > 0
            ? ConfigLoadResult<PuddingLlmProvidersConfig>.Fail(errors)
            : ConfigLoadResult<PuddingLlmProvidersConfig>.Ok(config);
    }

    /// <summary>加载系统配置。</summary>
    public async Task<ConfigLoadResult<PuddingSystemConfig>> LoadSystemAsync(CancellationToken ct = default)
    {
        var loadResult = await LoadJsonAsync<PuddingSystemConfig>(
            _paths.SystemConfigFile("system.json"),
            ct);

        if (!loadResult.Success)
            return loadResult;

        return ConfigLoadResult<PuddingSystemConfig>.Ok(loadResult.Config!);
    }

    /// <summary>加载安全配置。</summary>
    public async Task<ConfigLoadResult<PuddingSecurityConfig>> LoadSecurityAsync(CancellationToken ct = default)
    {
        var loadResult = await LoadJsonAsync<PuddingSecurityConfig>(
            _paths.SystemConfigFile("security.json"),
            ct);

        if (!loadResult.Success)
            return loadResult;

        return ConfigLoadResult<PuddingSecurityConfig>.Ok(loadResult.Config!);
    }

    /// <summary>加载连接器配置。</summary>
    public async Task<ConfigLoadResult<PuddingConnectorsConfig>> LoadConnectorsAsync(CancellationToken ct = default)
    {
        var loadResult = await LoadJsonAsync<PuddingConnectorsConfig>(
            _paths.SystemConfigFile("connectors.json"),
            ct);

        if (!loadResult.Success)
            return loadResult;

        return ConfigLoadResult<PuddingConnectorsConfig>.Ok(loadResult.Config!);
    }

    /// <summary>底层 JSON 反序列化。文件不存在、JSON 损坏、IO 异常均返回 Fail，不抛异常。</summary>
    private async Task<ConfigLoadResult<T>> LoadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return ConfigLoadResult<T>.Fail($"File not found: {path}");

        try
        {
            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
            return config is null
                ? ConfigLoadResult<T>.Fail($"{Path.GetFileName(path)}: deserialization returned null")
                : ConfigLoadResult<T>.Ok(config);
        }
        catch (JsonException ex)
        {
            return ConfigLoadResult<T>.Fail($"{Path.GetFileName(path)}: JSON format error — {ex.Message}");
        }
        catch (IOException ex)
        {
            return ConfigLoadResult<T>.Fail($"{Path.GetFileName(path)}: IO error — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ConfigLoadResult<T>.Fail($"{Path.GetFileName(path)}: Access denied — {ex.Message}");
        }
    }

    /// <summary>验证 LLM 配置，返回错误列表（空列表表示通过）。</summary>
    public static List<string> ValidateLlmProviders(PuddingLlmProvidersConfig config)
    {
        var errors = new List<string>();

        if (config.Providers.Count == 0)
        {
            errors.Add("llm.providers.json must define at least one provider.");
            return errors;
        }

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
            {
                errors.Add("llm.providers.json contains a provider with empty providerId.");
                continue;
            }
            if (!providerIds.Add(provider.ProviderId))
            {
                errors.Add($"llm.providers.json contains duplicate providerId '{provider.ProviderId}'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                errors.Add($"llm.providers.json provider '{provider.ProviderId}' has empty baseUrl.");
            if (provider.MaxConcurrentRequests is <= 0)
                errors.Add($"llm.providers.json provider '{provider.ProviderId}' maxConcurrentRequests must be greater than zero.");
            if (provider.TokensPerMinute is <= 0)
                errors.Add($"llm.providers.json provider '{provider.ProviderId}' tokensPerMinute must be greater than zero.");
            if (provider.RequestsPerMinute is <= 0)
                errors.Add($"llm.providers.json provider '{provider.ProviderId}' requestsPerMinute must be greater than zero.");

            var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId))
                    errors.Add($"llm.providers.json provider '{provider.ProviderId}' contains a model with empty modelId.");
                else if (!modelIds.Add(model.ModelId))
                    errors.Add($"llm.providers.json provider '{provider.ProviderId}' contains duplicate modelId '{model.ModelId}'.");
                if (model.MaxConcurrentRequests is <= 0)
                    errors.Add($"llm.providers.json provider '{provider.ProviderId}' model '{model.ModelId}' maxConcurrentRequests must be greater than zero.");
            }
        }

        if (config.Profiles.Count == 0)
        {
            errors.Add("llm.providers.json must define at least one profile.");
            return errors;
        }

        foreach (var (profileId, profile) in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                errors.Add("llm.providers.json contains an empty profile id.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(profile.ProviderId))
                errors.Add($"llm.providers.json profile '{profileId}' has empty providerId.");
            if (string.IsNullOrWhiteSpace(profile.ModelId))
                errors.Add($"llm.providers.json profile '{profileId}' has empty modelId.");
            if (!ProviderHasModel(config, profile.ProviderId, profile.ModelId))
                errors.Add($"llm.providers.json profile '{profileId}' references missing provider/model '{profile.ProviderId}/{profile.ModelId}'.");
        }

        ValidateRole(errors, config, "roles.conscious", config.Roles.Conscious);
        ValidateRole(errors, config, "roles.subconscious", config.Roles.Subconscious);

        return errors;
    }

    private static void ValidateRole(List<string> errors, PuddingLlmProvidersConfig config, string roleName, string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            errors.Add($"llm.providers.json {roleName} must reference a profile.");
        else if (!config.Profiles.ContainsKey(profileId))
            errors.Add($"llm.providers.json {roleName} references missing profile '{profileId}'.");
    }

    private static bool ProviderHasModel(PuddingLlmProvidersConfig config, string providerId, string modelId)
    {
        var provider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider?.Models.Any(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
