using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件式 LLM Provider/Model 管理服务 — 读写 data/config/llm.providers.json。
/// 唯一事实来源：llm.providers.json 文件。
/// </summary>
public sealed class LlmProviderFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<LlmProviderFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LlmProviderFileService(PuddingDataPaths paths, ILogger<LlmProviderFileService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string ConfigPath => _paths.SystemConfigFile("llm.providers.json");

    /// <summary>读取完整 LLM 配置。</summary>
    public async Task<PuddingLlmProvidersConfig> LoadAsync(CancellationToken ct = default)
    {
        // 优先使用 AtomicFileWriter 的缓存友好读取
        var config = await AtomicFileWriter.ReadJsonAsync<PuddingLlmProvidersConfig>(ConfigPath, JsonOptions, ct);
        return config ?? new PuddingLlmProvidersConfig();
    }

    /// <summary>获取所有 Provider（不含 models）。</summary>
    public async Task<List<LlmProviderDto>> ListProvidersAsync(CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        return config.Providers.Select((p, idx) => new LlmProviderDto(
            Id: idx + 1,
            ProviderId: p.ProviderId,
            Name: p.Name,
            Protocol: p.Protocol,
            BaseUrl: p.BaseUrl,
            HasApiKey: !string.IsNullOrWhiteSpace(p.ApiKey) || !string.IsNullOrWhiteSpace(p.ApiKeyRef),
            Description: null,
            IsEnabled: p.IsEnabled,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        )).ToList();
    }

    /// <summary>获取单个 Provider 详情（含 models 和 quota）。</summary>
    public async Task<LlmProviderDetailDto?> GetProviderAsync(string providerId, CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        var p = config.Providers.FirstOrDefault(x =>
            string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (p is null) return null;

        return new LlmProviderDetailDto(
            Id: config.Providers.IndexOf(p) + 1,
            ProviderId: p.ProviderId,
            Name: p.Name,
            Protocol: p.Protocol,
            BaseUrl: p.BaseUrl,
            HasApiKey: !string.IsNullOrWhiteSpace(p.ApiKey) || !string.IsNullOrWhiteSpace(p.ApiKeyRef),
            Description: null,
            IsEnabled: p.IsEnabled,
            Quota: p.IsEnabled ? new LlmProviderQuotaDto(null, null, 0, 0, false, null, null, DateTimeOffset.UtcNow) : null,
            Models: p.Models.Select(m => new LlmModelDto(
                Id: 0,
                ProviderId: 0,
                ModelId: m.ModelId,
                Name: m.Name,
                Description: null,
                MaxContextTokens: m.MaxContextTokens ?? 0,
                MaxOutputTokens: m.MaxOutputTokens ?? 0,
                InputPricePer1MTokens: m.PricePer1MInputTokens ?? 0,
                OutputPricePer1MTokens: m.PricePer1MOutputTokens ?? 0,
                CacheHitPricePer1MTokens: 0,
                CapabilityTags: m.CapabilityTags,
                IsDeprecated: m.IsDeprecated,
                IsDefault: m.IsDefault,
                SortOrder: m.SortOrder,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            )).ToList(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        );
    }

    /// <summary>创建 Provider。</summary>
    public async Task<LlmProviderDto> CreateProviderAsync(UpsertLlmProviderRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);

            if (config.Providers.Any(p => string.Equals(p.ProviderId, req.ProviderId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"ProviderId '{req.ProviderId}' 已存在");

            var newProvider = new PuddingLlmProviderConfig
            {
                ProviderId = req.ProviderId,
                Name = req.Name,
                Protocol = req.Protocol,
                BaseUrl = req.BaseUrl,
                ApiKey = req.ApiKey,
                IsEnabled = req.IsEnabled,
            };

            config.Providers.Add(newProvider);
            await SaveConfigAsync(config, ct);

            return new LlmProviderDto(
                Id: config.Providers.Count,
                ProviderId: newProvider.ProviderId,
                Name: newProvider.Name,
                Protocol: newProvider.Protocol,
                BaseUrl: newProvider.BaseUrl,
                HasApiKey: !string.IsNullOrWhiteSpace(newProvider.ApiKey) || !string.IsNullOrWhiteSpace(newProvider.ApiKeyRef),
                Description: null,
                IsEnabled: newProvider.IsEnabled,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>更新 Provider。</summary>
    public async Task<LlmProviderDto> UpdateProviderAsync(string providerId, UpsertLlmProviderRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

            if (p is null)
                throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

            config.Providers.Remove(p);
            var updated = p with
            {
                Name = req.Name,
                Protocol = req.Protocol,
                BaseUrl = req.BaseUrl,
                ApiKey = req.ApiKey ?? p.ApiKey,
                IsEnabled = req.IsEnabled,
            };
            config.Providers.Add(updated);

            await SaveConfigAsync(config, ct);

            return new LlmProviderDto(
                Id: config.Providers.Count,
                ProviderId: updated.ProviderId,
                Name: updated.Name,
                Protocol: updated.Protocol,
                BaseUrl: updated.BaseUrl,
                HasApiKey: !string.IsNullOrWhiteSpace(updated.ApiKey) || !string.IsNullOrWhiteSpace(updated.ApiKeyRef),
                Description: null,
                IsEnabled: updated.IsEnabled,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除 Provider。</summary>
    public async Task DeleteProviderAsync(string providerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

            if (p is null)
                throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

            config.Providers.Remove(p);

            // 清理引用此 Provider 的 profile
            var profilesToRemove = config.Profiles
                .Where(kvp => string.Equals(kvp.Value.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in profilesToRemove)
                config.Profiles.Remove(key);

            // 如果 roles 引用了被删除的 profile，清除（通过 with 创建新 config）
            var updatedRoles = config.Roles;
            if (config.Roles.Conscious is not null && profilesToRemove.Contains(config.Roles.Conscious))
                updatedRoles = updatedRoles with { Conscious = null };
            if (config.Roles.Subconscious is not null && profilesToRemove.Contains(config.Roles.Subconscious))
                updatedRoles = updatedRoles with { Subconscious = null };
            config = config with { Roles = updatedRoles };

            await SaveConfigAsync(config, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── Model Operations ─────────────────────────────────

    /// <summary>获取 Provider 下的所有模型。</summary>
    public async Task<List<LlmModelDto>> ListModelsAsync(string providerId, CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        var p = config.Providers.FirstOrDefault(x =>
            string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (p is null) return [];

        return p.Models.Select((m, idx) => new LlmModelDto(
            Id: idx + 1,
            ProviderId: 0,
            ModelId: m.ModelId,
            Name: m.Name,
            Description: null,
            MaxContextTokens: m.MaxContextTokens ?? 0,
            MaxOutputTokens: m.MaxOutputTokens ?? 0,
            InputPricePer1MTokens: m.PricePer1MInputTokens ?? 0,
            OutputPricePer1MTokens: m.PricePer1MOutputTokens ?? 0,
            CacheHitPricePer1MTokens: 0,
            CapabilityTags: m.CapabilityTags,
            IsDeprecated: m.IsDeprecated,
            IsDefault: m.IsDefault,
            SortOrder: m.SortOrder,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        )).ToList();
    }

    /// <summary>在 Provider 下创建模型。</summary>
    public async Task<LlmModelDto> CreateModelAsync(string providerId, UpsertLlmModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

            if (p.Models.Any(m => string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"ModelId '{req.ModelId}' 在该 Provider 下已存在");

            var newModel = new PuddingLlmModelConfig
            {
                ModelId = req.ModelId,
                Name = req.Name,
                MaxContextTokens = req.MaxContextTokens,
                MaxOutputTokens = req.MaxOutputTokens,
                PricePer1MInputTokens = req.InputPricePer1MTokens,
                PricePer1MOutputTokens = req.OutputPricePer1MTokens,
                CapabilityTags = req.CapabilityTags ?? [],
                IsDefault = req.IsDefault,
                IsDeprecated = req.IsDeprecated,
                SortOrder = req.SortOrder,
            };

            p.Models.Add(newModel);
            await SaveConfigAsync(config, ct);

            return new LlmModelDto(
                Id: p.Models.Count,
                ProviderId: 0,
                ModelId: newModel.ModelId,
                Name: newModel.Name,
                Description: null,
                MaxContextTokens: newModel.MaxContextTokens ?? 0,
                MaxOutputTokens: newModel.MaxOutputTokens ?? 0,
                InputPricePer1MTokens: newModel.PricePer1MInputTokens ?? 0,
                OutputPricePer1MTokens: newModel.PricePer1MOutputTokens ?? 0,
                CacheHitPricePer1MTokens: 0,
                CapabilityTags: newModel.CapabilityTags,
                IsDeprecated: newModel.IsDeprecated,
                IsDefault: newModel.IsDefault,
                SortOrder: newModel.SortOrder,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>更新模型。</summary>
    public async Task<LlmModelDto> UpdateModelAsync(string providerId, string modelId, UpsertLlmModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

            var m = p.Models.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
                throw new KeyNotFoundException($"Model '{modelId}' 不存在");

            p.Models.Remove(m);
            var updated = m with
            {
                Name = req.Name,
                MaxContextTokens = req.MaxContextTokens,
                MaxOutputTokens = req.MaxOutputTokens,
                PricePer1MInputTokens = req.InputPricePer1MTokens,
                PricePer1MOutputTokens = req.OutputPricePer1MTokens,
                CapabilityTags = req.CapabilityTags ?? [],
                IsDefault = req.IsDefault,
                IsDeprecated = req.IsDeprecated,
                SortOrder = req.SortOrder,
            };
            p.Models.Add(updated);
            await SaveConfigAsync(config, ct);

            return new LlmModelDto(
                Id: p.Models.IndexOf(updated) + 1,
                ProviderId: 0,
                ModelId: updated.ModelId,
                Name: updated.Name,
                Description: null,
                MaxContextTokens: updated.MaxContextTokens ?? 0,
                MaxOutputTokens: updated.MaxOutputTokens ?? 0,
                InputPricePer1MTokens: updated.PricePer1MInputTokens ?? 0,
                OutputPricePer1MTokens: updated.PricePer1MOutputTokens ?? 0,
                CacheHitPricePer1MTokens: 0,
                CapabilityTags: updated.CapabilityTags,
                IsDeprecated: updated.IsDeprecated,
                IsDefault: updated.IsDefault,
                SortOrder: updated.SortOrder,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除模型。</summary>
    public async Task DeleteModelAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

            var m = p.Models.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
                throw new KeyNotFoundException($"Model '{modelId}' 不存在");

            p.Models.Remove(m);
            await SaveConfigAsync(config, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── 内部方法 ─────────────────────────────────────────

    private async Task SaveConfigAsync(PuddingLlmProvidersConfig config, CancellationToken ct)
    {
        var errors = PuddingFileConfigLoader.ValidateLlmProviders(config);
        if (errors.Count > 0)
        {
            var errorSummary = string.Join("; ", errors);
            _logger.LogError("LLM config validation failed before write: {Errors}", errorSummary);
            throw new InvalidOperationException($"配置验证失败: {errorSummary}");
        }

        await AtomicFileWriter.WriteJsonAsync(ConfigPath, config, JsonOptions, ct);
        _logger.LogInformation("LLM config saved to {Path}", ConfigPath);
    }
}
