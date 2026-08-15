using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IKeyVaultService? _keyVaultService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LlmProviderFileService(
        PuddingDataPaths paths,
        ILogger<LlmProviderFileService> logger,
        ILlmConfigService? llmConfigService = null,
        IHttpClientFactory? httpClientFactory = null,
        IKeyVaultService? keyVaultService = null)
    {
        _paths = paths;
        _logger = logger;
        _llmConfigService = llmConfigService;
        _httpClientFactory = httpClientFactory;
        _keyVaultService = keyVaultService;
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
                Protocol: m.Protocol,
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
                UpdatedAt: DateTimeOffset.UtcNow,
                MaxInputTokens: m.MaxInputTokens
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
                BaseUrl = req.BaseUrl,
                ApiKey = req.ApiKey,
                IsEnabled = req.IsEnabled,
                Description = req.Description,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
                TokensPerMinute = req.TokensPerMinute,
                RequestsPerMinute = req.RequestsPerMinute,
                                RequestTimeoutSeconds = req.RequestTimeoutSeconds,
                StreamTimeoutSeconds = req.StreamTimeoutSeconds,
                Compat = MapCompatFromRequest(req.Compat),
            };

            config.Providers.Add(newProvider);
            await SaveConfigAsync(config, ct);

            return new LlmProviderDto(
                Id: config.Providers.Count,
                ProviderId: newProvider.ProviderId,
                Name: newProvider.Name,
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
                BaseUrl = req.BaseUrl,
                ApiKey = req.ApiKey ?? p.ApiKey,
                IsEnabled = req.IsEnabled,
                Description = req.Description,
                MaxConcurrentRequests = req.MaxConcurrentRequests,
                TokensPerMinute = req.TokensPerMinute,
                RequestsPerMinute = req.RequestsPerMinute,
                                RequestTimeoutSeconds = req.RequestTimeoutSeconds ?? p.RequestTimeoutSeconds,
                StreamTimeoutSeconds = req.StreamTimeoutSeconds ?? p.StreamTimeoutSeconds,
                Compat = MapCompatFromRequest(req.Compat) ?? p.Compat,
            };
            config.Providers.Add(updated);

            await SaveConfigAsync(config, ct);

            return new LlmProviderDto(
                Id: config.Providers.Count,
                ProviderId: updated.ProviderId,
                Name: updated.Name,
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
                BaseUrl = providerRequest.BaseUrl,
                ApiKey = providerRequest.ApiKey ?? provider.ApiKey,
                IsEnabled = providerRequest.IsEnabled,
                Description = providerRequest.Description,
                MaxConcurrentRequests = providerRequest.MaxConcurrentRequests,
                TokensPerMinute = providerRequest.TokensPerMinute,
                RequestsPerMinute = providerRequest.RequestsPerMinute,
                                RequestTimeoutSeconds = providerRequest.RequestTimeoutSeconds ?? provider.RequestTimeoutSeconds,
                StreamTimeoutSeconds = providerRequest.StreamTimeoutSeconds ?? provider.StreamTimeoutSeconds,
                Compat = MapCompatFromRequest(providerRequest.Compat) ?? provider.Compat,
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
            Protocol: m.Protocol,
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
            UpdatedAt: DateTimeOffset.UtcNow,
            MaxInputTokens: m.MaxInputTokens
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
                Protocol = req.Protocol,
                MaxContextTokens = req.MaxContextTokens,
                MaxInputTokens = req.MaxInputTokens,
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
                Protocol: newModel.Protocol,
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
                UpdatedAt: DateTimeOffset.UtcNow,
                MaxInputTokens: newModel.MaxInputTokens
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
                Protocol = req.Protocol,
                MaxContextTokens = req.MaxContextTokens,
                MaxInputTokens = req.MaxInputTokens,
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
                Protocol: updated.Protocol,
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
                UpdatedAt: DateTimeOffset.UtcNow,
                MaxInputTokens: updated.MaxInputTokens
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
                Protocol = req.Protocol,
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

    private static PuddingProviderCompatConfig? MapCompatFromRequest(ProviderCompatRequest? src)
    {
        if (src is null) return null;
        return new PuddingProviderCompatConfig
        {
            MaxTokensField = src.MaxTokensField ?? "max_completion_tokens",
            RequiresStringContent = src.RequiresStringContent,
            UseReasoningEffort = src.UseReasoningEffort,
            DefaultReasoningEffort = src.DefaultReasoningEffort,
            SupportsUsageInStreaming = src.SupportsUsageInStreaming,
            RequiresReasoningContentInToolMessages = src.RequiresReasoningContentInToolMessages,
        };
    }

    // ─── Provider Balance Query (DeepSeek get-user-balance 等 OpenAI 兼容 provider) ──

    /// <summary>Named HttpClient used for live balance queries.</summary>
    public const string BalanceHttpClientName = "LlmBalanceQuery";

    /// <summary>
    /// 查询 provider 账户余额（DeepSeek: GET {baseUrl}/user/balance）。
    /// 支持 DeepSeek、OpenAI 等 OpenAI 兼容协议服务商——只要 provider 暴露 /user/balance 端点。
    /// apiKey 优先从 llm.providers.json ApiKey 字段读取，按需展开 ${ENV_VAR} 占位符与
    /// {{vault:NAME}} 密钥注入；找不到 ApiKey 时回退到 ApiKeyRef 经 KeyVault 解析。
    /// apiKey 不会出现在任何日志中（仅记录 providerId、状态码、耗时等）。
    /// </summary>
    public async Task<LlmProviderBalanceDto> GetBalanceAsync(
        string providerId,
        CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        var provider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
            throw new KeyNotFoundException($"Provider '{providerId}' 不存在");

        var apiKey = await ResolveApiKeyForBalanceAsync(provider, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"Provider '{providerId}' 未配置 ApiKey（既无明文 apiKey 也无有效的 ApiKeyRef），无法查询余额。");

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        // DeepSeek 余额端点：/user/balance（注意：不带 /v1）。
        var url = baseUrl.EndsWith("/user/balance", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/user/balance";

        var httpClient = _httpClientFactory?.CreateClient(BalanceHttpClientName) ?? new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = TryParseBalanceErrorMessage(body)
                    ?? $"余额 API 返回状态码 {(int)response.StatusCode}";
                _logger.LogWarning(
                    "[LlmBalance] FAIL provider={ProviderId} status={Status} elapsed={ElapsedMs}ms",
                    providerId, (int)response.StatusCode, sw.ElapsedMilliseconds);
                return new LlmProviderBalanceDto(
                    provider.ProviderId, url,
                    IsAvailable: false, [], Error: errorMessage, QueriedAt: DateTimeOffset.UtcNow);
            }

            var parsed = ParseBalanceResponse(body);
            _logger.LogInformation(
                "[LlmBalance] OK provider={ProviderId} available={IsAvailable} infos={Count} elapsed={ElapsedMs}ms",
                providerId, parsed.IsAvailable, parsed.BalanceInfos.Count, sw.ElapsedMilliseconds);
            return new LlmProviderBalanceDto(
                provider.ProviderId, url,
                parsed.IsAvailable, parsed.BalanceInfos,
                Error: null, QueriedAt: DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex, "[LlmBalance] HTTP ERROR provider={ProviderId} url={Url} elapsed={ElapsedMs}ms",
                providerId, url, sw.ElapsedMilliseconds);
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                ex, "[LlmBalance] TIMEOUT provider={ProviderId} url={Url} elapsed={ElapsedMs}ms",
                providerId, url, sw.ElapsedMilliseconds);
            throw new HttpRequestException("余额查询请求超时", ex);
        }
    }

    /// <summary>
    /// 解析 provider 的 apiKey——按顺序展开 ${ENV_VAR} 占位符、解析 {{vault:NAME}} 注入、
    /// 最后回退到 ApiKeyRef 经 KeyVault 解析。失败返回 null，但不抛异常。
    /// </summary>
    private async Task<string?> ResolveApiKeyForBalanceAsync(
        PuddingLlmProviderConfig provider, CancellationToken ct)
    {
        var raw = provider.ApiKey;

        // 1. 展开 ${ENV_VAR} 占位符（如 "${DASHSCOPE_API_KEY}"）
        if (!string.IsNullOrWhiteSpace(raw))
            raw = ExpandEnvPlaceholders(raw);

        // 2. 解析 {{vault:NAME}} 注入占位符（仅当 KeyVault 服务可用时）
        if (!string.IsNullOrWhiteSpace(raw)
            && raw.Contains("{{vault:", StringComparison.OrdinalIgnoreCase)
            && _keyVaultService is not null)
        {
            raw = await NormalizeKeyVaultInjectAsync(raw, ct);
        }

        if (!string.IsNullOrWhiteSpace(raw))
            return raw;

        // 3. ApiKeyRef → KeyVault 解析
        if (!string.IsNullOrWhiteSpace(provider.ApiKeyRef) && _keyVaultService is not null)
        {
            var keyVaultId = NormalizeKeyVaultIdForBalance(provider.ApiKeyRef);
            try
            {
                var secret = await _keyVaultService.GetSecretAsync(keyVaultId, includePlainText: true, ct);
                if (!string.IsNullOrWhiteSpace(secret?.Value))
                    return secret.Value;

                // GetSecret 返回空时尝试通过 {{vault:ID}} 注入路径解析
                var placeholder = $"{{{{vault:{keyVaultId}}}}}";
                var injected = await NormalizeKeyVaultInjectAsync(placeholder, ct);
                if (!string.Equals(injected, placeholder, StringComparison.Ordinal))
                    return injected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "[LlmBalance] KeyVault 查询失败 provider={ProviderId} ref={KeyVaultId}",
                    provider.ProviderId, keyVaultId);
            }
        }

        return raw;
    }

    private async Task<string> NormalizeKeyVaultInjectAsync(string text, CancellationToken ct)
    {
        if (_keyVaultService is null) return text;
        var injected = await _keyVaultService.InjectAsync(text, ct);
        return string.IsNullOrWhiteSpace(injected) ? text : injected;
    }

    /// <summary>ApiKeyRef 转换为 KeyVaultId：去掉可选的 "vault:" 前缀。</summary>
    private static string NormalizeKeyVaultIdForBalance(string? apiKeyRef)
    {
        if (string.IsNullOrWhiteSpace(apiKeyRef)) return string.Empty;
        const string prefix = "vault:";
        return apiKeyRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? apiKeyRef[prefix.Length..]
            : apiKeyRef;
    }

    // ── ${ENV_VAR} → 环境变量值 ──

    private static readonly Regex EnvPlaceholderRegex =
        new(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>将 ${VAR} 占位符展开为环境变量值；占位符不存在或环境变量未设置时保留原文本。</summary>
    private static string ExpandEnvPlaceholders(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
            return value;

        return EnvPlaceholderRegex.Replace(value, m =>
        {
            var envValue = Environment.GetEnvironmentVariable(m.Groups["name"].Value);
            return string.IsNullOrEmpty(envValue) ? m.Value : envValue;
        });
    }

    // ── DeepSeek 余额响应 JSON 解析 ──

    /// <summary>
    /// 解析成功响应：{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}。
    /// </summary>
    private static (bool IsAvailable, List<LlmBalanceInfoDto> BalanceInfos) ParseBalanceResponse(string json)
    {
        var balanceInfos = new List<LlmBalanceInfoDto>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var isAvailable = root.TryGetProperty("is_available", out var avail)
                              && avail.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("balance_infos", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    balanceInfos.Add(new LlmBalanceInfoDto(
                        Currency: item.TryGetProperty("currency", out var c)
                            ? c.GetString() ?? "UNKNOWN"
                            : "UNKNOWN",
                        TotalBalance: TryGetDecimal(item, "total_balance"),
                        GrantedBalance: TryGetDecimal(item, "granted_balance"),
                        ToppedUpBalance: TryGetDecimal(item, "topped_up_balance")));
                }
            }

            return (isAvailable, balanceInfos);
        }
        catch (JsonException)
        {
            return (false, balanceInfos);
        }
    }

    private static decimal TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var element))
            return 0m;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var dec))
            return dec;

        // DeepSeek 实际返回字符串数字（如 "110.00"）——回退到字符串解析
        var str = element.GetString();
        return string.IsNullOrEmpty(str) || !decimal.TryParse(str, out var parsed)
            ? 0m
            : parsed;
    }

    /// <summary>
    /// 解析失败响应：{"error":{"message":"...","type":"authentication_error","param":null,"code":"invalid_request_error"}}。
    /// </summary>
    private static string? TryParseBalanceErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch (JsonException)
        {
            // 忽略非 JSON 错误响应，由调用方回退到状态码消息
        }
        return null;
    }
}
