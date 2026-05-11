using System.Text.Json;

namespace PuddingRuntime.Services;

/// <summary>
/// JSON 配置文件加载器。优先级：JSON > 环境变量 > 默认值。
/// JSON 文件是 LLM 配置的唯一底层来源。
/// </summary>
public static class PuddingConfigLoader
{
    public const string DefaultPath = "/app/data/conf/pudding-config.json";

    private static PuddingJsonConfig? _cached;
    private static DateTime _lastLoad = DateTime.MinValue;
    private static readonly object _lock = new();

    /// <summary>从 JSON 文件加载配置（缓存 5 秒）。</summary>
    public static PuddingJsonConfig? Load(string? path = null)
    {
        path ??= DefaultPath;
        lock (_lock)
        {
            if (_cached is not null && (DateTime.UtcNow - _lastLoad).TotalSeconds < 5)
                return _cached;
        }

        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize(json, PuddingJsonConfigContext.Default.PuddingJsonConfig);
            lock (_lock)
            {
                _cached = config;
                _lastLoad = DateTime.UtcNow;
            }
            return config;
        }
        catch
        {
            return null;
        }
    }

    public static (string Endpoint, string ApiKey, string Model) ResolveConscious()
    {
        var json = Load();
        if (json?.Llm?.Conscious is { } c && !string.IsNullOrWhiteSpace(c.ApiKey))
            return (c.Endpoint ?? "https://api.openai.com/v1", c.ApiKey, c.ModelId ?? "gpt-4o-mini");

        return (Env("LLM_ENDPOINT", "https://api.openai.com/v1"), Env("LLM_API_KEY", ""), Env("LLM_MODEL", "gpt-4o-mini"));
    }

    public static (string Endpoint, string ApiKey, string Model) ResolveMemory()
    {
        var json = Load();
        if (json?.Llm?.Memory is { } m && !string.IsNullOrWhiteSpace(m.ApiKey))
            return (m.Endpoint ?? "https://api.openai.com/v1", m.ApiKey, m.ModelId ?? "gpt-4o-mini");

        // 回退到 conscious
        if (json?.Llm?.Conscious is { } c && !string.IsNullOrWhiteSpace(c.ApiKey))
            return (c.Endpoint ?? "https://api.openai.com/v1", c.ApiKey, c.ModelId ?? "gpt-4o-mini");

        return (Env("MEMORY_LLM_ENDPOINT", "https://api.openai.com/v1"), Env("MEMORY_LLM_API_KEY", ""), Env("MEMORY_LLM_MODEL_ID", "gpt-4o-mini"));
    }

    private static string Env(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name) ?? defaultValue;
}
