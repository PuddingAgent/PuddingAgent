using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件式 LLM Provider/Model 管理服务 — 读写 data/config/llm.providers.json。
/// 唯一事实来源：llm.providers.json 文件。
/// </summary>
public sealed class LlmProviderFileService : ILlmResourcePoolService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<LlmProviderFileService> _logger;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LlmProviderFileService(
        PuddingDataPaths paths,
        ILogger<LlmProviderFileService> logger,
        ILlmConfigService? llmConfigService = null)
    {
        _paths = paths;
        _logger = logger;
        _llmConfigService = llmConfigService;
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
            Description: p.Description,
            IsEnabled: p.IsEnabled,
            MaxConcurrentRequests: p.MaxConcurrentRequests,
            TokensPerMinute: p.TokensPerMinute,
            RequestsPerMinute: p.RequestsPerMinute,
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
            Description: p.Description,
            IsEnabled: p.IsEnabled,
            MaxConcurrentRequests: p.MaxConcurrentRequests,
            TokensPerMinute: p.TokensPerMinute,
            RequestsPerMinute: p.RequestsPerMinute,
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
                CacheHitPricePer1MTokens: m.PricePer1MCacheHitTokens ?? 0,
                CapabilityTags: m.CapabilityTags,
                IsDeprecated: m.IsDeprecated,
                IsDefault: m.IsDefault,
                IsEmbedding: m.IsEmbedding,
                SortOrder: m.SortOrder,
                MaxConcurrentRequests: m.MaxConcurrentRequests,
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
                Description = req.Description,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
                TokensPerMinute = req.TokensPerMinute,
                RequestsPerMinute = req.RequestsPerMinute,
                RequestTimeoutSeconds = req.RequestTimeoutSeconds,
                StreamTimeoutSeconds = req.StreamTimeoutSeconds,
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
                Description: newProvider.Description,
                IsEnabled: newProvider.IsEnabled,
                MaxConcurrentRequests: newProvider.MaxConcurrentRequests,
                TokensPerMinute: newProvider.TokensPerMinute,
                RequestsPerMinute: newProvider.RequestsPerMinute,
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
                Description = req.Description,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
                TokensPerMinute = req.TokensPerMinute,
                RequestsPerMinute = req.RequestsPerMinute,
                RequestTimeoutSeconds = req.RequestTimeoutSeconds ?? p.RequestTimeoutSeconds,
                StreamTimeoutSeconds = req.StreamTimeoutSeconds ?? p.StreamTimeoutSeconds,
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
                Description: updated.Description,
                IsEnabled: updated.IsEnabled,
                MaxConcurrentRequests: updated.MaxConcurrentRequests,
                TokensPerMinute: updated.TokensPerMinute,
                RequestsPerMinute: updated.RequestsPerMinute,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>一次性创建或更新 Provider 及其模型，避免写入缺少模型的无效中间配置。</summary>
    public async Task<LlmProviderDto> UpsertProviderWithModelsAsync(
        UpsertLlmProviderRequest providerRequest,
        IReadOnlyList<UpsertLlmModelRequest> modelRequests,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var existing = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerRequest.ProviderId, StringComparison.OrdinalIgnoreCase));

            var provider = existing ?? new PuddingLlmProviderConfig
            {
                ProviderId = providerRequest.ProviderId,
            };

                        provider = provider with
            {
                Name = providerRequest.Name,
                Protocol = providerRequest.Protocol,
                BaseUrl = providerRequest.BaseUrl,
                ApiKey = providerRequest.ApiKey ?? provider.ApiKey,
                IsEnabled = providerRequest.IsEnabled,
                Description = providerRequest.Description,
                MaxConcurrentRequests = providerRequest.MaxConcurrentRequests,
                TokensPerMinute = providerRequest.TokensPerMinute,
                RequestsPerMinute = providerRequest.RequestsPerMinute,
                RequestTimeoutSeconds = providerRequest.RequestTimeoutSeconds ?? provider.RequestTimeoutSeconds,
                StreamTimeoutSeconds = providerRequest.StreamTimeoutSeconds ?? provider.StreamTimeoutSeconds,
                Models = MergeModels(provider.Models, modelRequests),
            };

            if (existing is not null)
                config.Providers.Remove(existing);
            config.Providers.Add(provider);

            await SaveConfigAsync(config, ct);

            return new LlmProviderDto(
                Id: config.Providers.IndexOf(provider) + 1,
                ProviderId: provider.ProviderId,
                Name: provider.Name,
                Protocol: provider.Protocol,
                BaseUrl: provider.BaseUrl,
                HasApiKey: !string.IsNullOrWhiteSpace(provider.ApiKey) || !string.IsNullOrWhiteSpace(provider.ApiKeyRef),
                Description: provider.Description,
                IsEnabled: provider.IsEnabled,
                MaxConcurrentRequests: provider.MaxConcurrentRequests,
                TokensPerMinute: provider.TokensPerMinute,
                RequestsPerMinute: provider.RequestsPerMinute,
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
            CacheHitPricePer1MTokens: m.PricePer1MCacheHitTokens ?? 0,
            CapabilityTags: m.CapabilityTags,
            IsDeprecated: m.IsDeprecated,
            IsDefault: m.IsDefault,
            IsEmbedding: m.IsEmbedding,
            SortOrder: m.SortOrder,
            MaxConcurrentRequests: m.MaxConcurrentRequests,
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
                PricePer1MCacheHitTokens = req.CacheHitPricePer1MTokens,
                CapabilityTags = req.CapabilityTags ?? [],
                IsDefault = req.IsDefault,
                IsDeprecated = req.IsDeprecated,
                IsEmbedding = req.IsEmbedding,
                SortOrder = req.SortOrder,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
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
                CacheHitPricePer1MTokens: newModel.PricePer1MCacheHitTokens ?? 0,
                CapabilityTags: newModel.CapabilityTags,
                IsDeprecated: newModel.IsDeprecated,
                IsDefault: newModel.IsDefault,
                IsEmbedding: newModel.IsEmbedding,
                SortOrder: newModel.SortOrder,
                MaxConcurrentRequests: newModel.MaxConcurrentRequests,
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
                PricePer1MCacheHitTokens = req.CacheHitPricePer1MTokens,
                CapabilityTags = req.CapabilityTags ?? [],
                IsDefault = req.IsDefault,
                IsDeprecated = req.IsDeprecated,
                IsEmbedding = req.IsEmbedding,
                SortOrder = req.SortOrder,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
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
                CacheHitPricePer1MTokens: updated.PricePer1MCacheHitTokens ?? 0,
                CapabilityTags: updated.CapabilityTags,
                IsDeprecated: updated.IsDeprecated,
                IsDefault: updated.IsDefault,
                IsEmbedding: updated.IsEmbedding,
                SortOrder: updated.SortOrder,
                MaxConcurrentRequests: updated.MaxConcurrentRequests,
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

    private static List<PuddingLlmModelConfig> MergeModels(
        List<PuddingLlmModelConfig> existingModels,
        IReadOnlyList<UpsertLlmModelRequest> modelRequests)
    {
        var models = existingModels.ToList();
        foreach (var req in modelRequests)
        {
            var existing = models.FirstOrDefault(m =>
                string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                models.Remove(existing);

            if (req.IsDefault)
            {
                models = models
                    .Select(m => m with { IsDefault = false })
                    .ToList();
            }

            models.Add(new PuddingLlmModelConfig
            {
                ModelId = req.ModelId,
                Name = req.Name,
                MaxContextTokens = req.MaxContextTokens,
                MaxOutputTokens = req.MaxOutputTokens,
                PricePer1MInputTokens = req.InputPricePer1MTokens,
                PricePer1MOutputTokens = req.OutputPricePer1MTokens,
                PricePer1MCacheHitTokens = req.CacheHitPricePer1MTokens,
                CapabilityTags = req.CapabilityTags ?? [],
                IsDefault = req.IsDefault || existing?.IsDefault == true,
                IsDeprecated = req.IsDeprecated,
                SortOrder = req.SortOrder,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
            });
        }

        return models
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task SaveConfigAsync(PuddingLlmProvidersConfig config, CancellationToken ct)
    {
        // 第一性原理：自动从 Providers.Models 中扫描 IsEmbedding 模型并填充 Embedding 节。
        // 确保 llm.providers.json 始终由代码托管，而非依赖手动配置。
        config = AutoPopulateEmbedding(config);

        var errors = PuddingFileConfigLoader.ValidateLlmProviders(config);
        if (errors.Count > 0)
        {
            var errorSummary = string.Join("; ", errors);
            _logger.LogError("LLM config validation failed before write: {Errors}", errorSummary);
            throw new InvalidOperationException($"配置验证失败: {errorSummary}");
        }

        await AtomicFileWriter.WriteJsonAsync(ConfigPath, config, JsonOptions, ct);
        _logger.LogInformation("LLM config saved to {Path}", ConfigPath);

        // 写入完成后立即通知内存缓存重新加载，实现热更新
        _llmConfigService?.Reload(config);
    }

    /// <summary>
    /// 从 Providers 列表中自动发现 Embedding 模型并填充配置。
    /// 如果已显式配置 Embedding 节，直接返回原配置。
    /// </summary>
    private static PuddingLlmProvidersConfig AutoPopulateEmbedding(PuddingLlmProvidersConfig config)
    {
        // 已显式配置 → 保留用户设置
        if (config.Embedding is not null && !string.IsNullOrWhiteSpace(config.Embedding.ProviderId))
            return config;

        // 扫描所有 enabled provider 的模型，找第一个 IsEmbedding=true 的非废弃模型
        foreach (var provider in config.Providers.Where(p => p.IsEnabled))
        {
            var embeddingModel = provider.Models
                .FirstOrDefault(m => !m.IsDeprecated && m.IsEmbedding);

            if (embeddingModel is not null)
            {
                return config with
                {
                    Embedding = new PuddingLlmEmbeddingConfig
                    {
                        ProviderId = provider.ProviderId,
                        ModelId = embeddingModel.ModelId,
                        Dimension = embeddingModel.MaxOutputTokens, // 取 maxOutputTokens 作为向量维度参考
                    }
                };
            }
        }

        return config;
    }
}
