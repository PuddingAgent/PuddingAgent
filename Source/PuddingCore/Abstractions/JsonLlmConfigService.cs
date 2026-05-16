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
                IsDefault = m.IsDefault,
                IsDeprecated = m.IsDeprecated,
                SortOrder = m.SortOrder,
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

        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            ModelId = resolvedModelId ?? data.DefaultModelId ?? "gpt-4o-mini",
        };
    }

    public LlmConfig? GetDefault()
    {
        var data = GetData();
        var providerId = data.DefaultProviderId
            ?? data.Providers.FirstOrDefault(p => p.IsEnabled)?.ProviderId;

        if (string.IsNullOrWhiteSpace(providerId)) return null;

        return Resolve(providerId, data.DefaultModelId);
    }

    public LlmConfig? GetMemoryConfig()
    {
        var data = GetData();
        var mem = data.Memory;
        if (mem == null) return GetDefault(); // 回退到主 LLM 配置

        // 若独立配置了 memory endpoint/key，使用独立配置
        var providerId = mem.ProviderId ?? data.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(providerId)) return GetDefault();

        var provider = data.Providers.FirstOrDefault(p =>
            p.IsEnabled && p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null) return GetDefault();

        return new LlmConfig
        {
            Endpoint = mem.Endpoint ?? provider.BaseUrl,
            ApiKey = mem.ApiKey ?? provider.ApiKey,
            ModelId = mem.ModelId ?? data.DefaultModelId ?? "gpt-4o-mini",
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
}

internal sealed record LlmMemoryConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
}
