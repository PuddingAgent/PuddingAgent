using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// JSON 文件支持的 LLM 配置服务 — 从 data/llm/config.json 加载，
/// 内存缓存以最小化磁盘 I/O。
/// </summary>
public class JsonLlmConfigService : ILlmConfigService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private LlmConfigData? _data;
    private DateTime _lastLoad = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonLlmConfigService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<LlmProviderInfo> GetEnabledProviders()
    {
        var data = GetData();
        return data.Providers
            .Where(p => p.IsEnabled)
            .Select(p => new LlmProviderInfo
            {
                ProviderId = p.ProviderId,
                Name = p.Name,
                Protocol = p.Protocol,
                BaseUrl = p.BaseUrl,
                IsEnabled = p.IsEnabled,
                HasApiKey = !string.IsNullOrWhiteSpace(p.ApiKey),
            })
            .ToList();
    }

    public IReadOnlyList<LlmModelInfo> GetAllModels()
    {
        var data = GetData();
        return data.Models
            .Select(m => new LlmModelInfo
            {
                ModelId = m.ModelId,
                ProviderId = m.ProviderId,
                Name = m.Name,
                MaxContextTokens = m.MaxContextTokens,
                MaxOutputTokens = m.MaxOutputTokens,
                InputPricePer1MTokens = 0,
                OutputPricePer1MTokens = 0,
                CacheHitPricePer1MTokens = 0,
                IsDefault = m.IsDefault,
                IsDeprecated = m.IsDeprecated,
                IsEmbedding = false,
                SortOrder = m.SortOrder,
                MaxConcurrentRequests = m.MaxConcurrentRequests,
            })
            .ToList();
    }

    public LlmConfig? Resolve(string providerId, string? modelId = null)
    {
        var data = GetData();
        var provider = data.Providers.FirstOrDefault(p =>
            p.IsEnabled && p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null) return null;

        var resolvedModelId = modelId;
        if (string.IsNullOrWhiteSpace(resolvedModelId))
        {
            var defaultModel = data.Models.FirstOrDefault(m =>
                m.ProviderId == provider.ProviderId && m.IsDefault && !m.IsDeprecated);
            resolvedModelId = defaultModel?.ModelId
                ?? data.Models.FirstOrDefault(m =>
                    m.ProviderId == provider.ProviderId && !m.IsDeprecated)?.ModelId;
        }

        var model = data.Models.FirstOrDefault(m =>
            m.ProviderId == provider.ProviderId
            && !m.IsDeprecated
            && string.Equals(m.ModelId, resolvedModelId, StringComparison.OrdinalIgnoreCase));

        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            ModelId = resolvedModelId ?? data.DefaultModelId ?? "gpt-4o-mini",
            MaxContextTokens = model?.MaxContextTokens,
            MaxOutputTokens = model?.MaxOutputTokens,
        };
    }

    public LlmProfileInfo? ResolveProfile(string profileId)
        => null;

    public LlmConfig GetDefault()
    {
        var data = GetData();
        var providerId = data.DefaultProviderId
            ?? data.Providers.FirstOrDefault(p => p.IsEnabled)?.ProviderId;

        if (string.IsNullOrWhiteSpace(providerId))
            throw new InvalidOperationException(
                "No enabled LLM provider found. Configure at least one provider in data/config/llm.providers.json.");

        var resolved = Resolve(providerId, data.DefaultModelId);
        return resolved
            ?? throw new InvalidOperationException(
                $"Default provider '{providerId}' with model '{data.DefaultModelId}' could not be resolved.");
    }

    public LlmConfig? GetMemoryConfig()
    {
        var data = GetData();
        var mem = data.Memory;
        if (mem == null) return null;

        // 若独立配置了 memory endpoint/key，使用独立配置
        var providerId = mem.ProviderId ?? data.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(providerId)) return null;

        var provider = data.Providers.FirstOrDefault(p =>
            p.IsEnabled && p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null) return null;

        var resolvedModelId = mem.ModelId ?? data.DefaultModelId ?? "gpt-4o-mini";
        var model = data.Models.FirstOrDefault(m =>
            m.ProviderId == provider.ProviderId
            && !m.IsDeprecated
            && string.Equals(m.ModelId, resolvedModelId, StringComparison.OrdinalIgnoreCase));

        return new LlmConfig
        {
            Endpoint = mem.Endpoint ?? provider.BaseUrl,
            ApiKey = mem.ApiKey ?? provider.ApiKey,
            ModelId = resolvedModelId,
            MaxContextTokens = model?.MaxContextTokens,
            MaxOutputTokens = model?.MaxOutputTokens,
        };
    }

    public LlmConfig? GetEmbeddingConfig()
    {
        // JsonLlmConfigService 不支持 Embedding 节
        return null;
    }

    public LlmProviderStrategy? GetProviderStrategy(string providerId)
    {
        var data = GetData();
        var provider = data.Providers.FirstOrDefault(p =>
            p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
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
        };
    }

    public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId)
    {
        var data = GetData();
        var model = data.Models.FirstOrDefault(m =>
            m.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
            m.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));

        // 模型级别优先：如果 JSON 中定义了 maxConcurrentRequests，使用模型级值作为 MaxConcurrentRequests
        // 其他策略字段仍从 Provider 继承
        var provider = data.Providers.FirstOrDefault(p =>
            p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));

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
        };
    }

    public void Reload()
    {
        lock (_lock)
        {
            _data = null;
            _lastLoad = DateTime.MinValue;
            Load();
        }
    }

    void ILlmConfigService.Reload(object config)
    {
        // JsonLlmConfigService does not support hot reload from typed config — no-op
        Reload();
    }

    private LlmConfigData GetData()
    {
        var now = DateTime.UtcNow;
        if (_data != null && (now - _lastLoad) < CacheTtl)
            return _data;

        Load();
        return _data!;
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException(
                    $"LLM config file not found: {_filePath}. Create data/llm/config.json with provider and model definitions.");

            var json = File.ReadAllText(_filePath);
            _data = JsonSerializer.Deserialize<LlmConfigData>(json, JsonOpts)
                ?? throw new InvalidOperationException($"Failed to parse LLM config: {_filePath}");
            _lastLoad = DateTime.UtcNow;
        }
    }
}

// ── JSON 反序列化模型 ───────────────────────────────────────

    internal sealed record LlmConfigData
{
    public List<LlmProviderEntry> Providers { get; init; } = [];
    public List<LlmModelEntry> Models { get; init; } = [];
    public string? DefaultProviderId { get; init; }
    public string? DefaultModelId { get; init; }
    public LlmMemoryConfig? Memory { get; init; }
}

internal sealed record LlmProviderEntry
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "openai";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public bool IsEnabled { get; init; } = true;

    public int? RequestTimeoutSeconds { get; init; }
    public int? StreamTimeoutSeconds { get; init; }
    public int? MaxRetries { get; init; }
    public int? RetryDelaySeconds { get; init; }
    public int? CircuitBreakerFailureThreshold { get; init; }
    public int? CircuitBreakerRecoverySeconds { get; init; }
    public int? MaxConcurrentRequests { get; init; }
    public long? TokensPerMinute { get; init; }
    public int? RequestsPerMinute { get; init; }
}

internal sealed record LlmModelEntry
{
    public string ModelId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public int MaxContextTokens { get; init; } = 1048576;
    public int MaxOutputTokens { get; init; } = 16384;
    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public int SortOrder { get; init; } = 10;
    public int? MaxConcurrentRequests { get; init; }
}

internal sealed record LlmMemoryConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
}
