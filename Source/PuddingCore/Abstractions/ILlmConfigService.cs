using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 统一 LLM 配置服务 — 所有 LLM 服务商、模型、密钥的唯一数据入口。
/// 
/// 设计原则：
///   · 单一来源：所有 LLM 配置从 JSON 文件读取，不使用 .env
///   · 内存缓存：启动时加载一次，减少磁盘 I/O
///   · 职责边界：其他服务/类不可直接读配置文件或数据库中的 LLM 配置
///   · DB 同步：启动时从 JSON 同步到 DB（供管理后台查询）
/// </summary>
public interface ILlmConfigService
{
    /// <summary>获取所有已启用的 provider。</summary>
    IReadOnlyList<LlmProviderInfo> GetEnabledProviders();

    /// <summary>获取所有已注册的 model。</summary>
    IReadOnlyList<LlmModelInfo> GetAllModels();

    /// <summary>根据 providerId + modelId 解析完整的 LlmConfig（Endpoint + ApiKey + ModelId）。</summary>
    LlmConfig? Resolve(string providerId, string? modelId = null);

    /// <summary>获取默认的 LLM 配置（defaultProviderId + defaultModelId）。</summary>
    LlmConfig? GetDefault();

    /// <summary>获取 memory/潜意识 LLM 的独立配置（若配置了独立 endpoint/key）。</summary>
    LlmConfig? GetMemoryConfig();

    /// <summary>重新从 JSON 文件加载配置（热重载）。</summary>
    void Reload();
}

/// <summary>Provider 信息（用于管理后台展示和 Agent 选择）。</summary>
public sealed record LlmProviderInfo
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "openai";
    public string BaseUrl { get; init; } = "";
    public bool IsEnabled { get; init; }
    public bool HasApiKey { get; init; }
}

/// <summary>Model 信息（用于管理后台展示和 Agent 选择）。</summary>
public sealed record LlmModelInfo
{
    public string ModelId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public int MaxContextTokens { get; init; }
    public int MaxOutputTokens { get; init; }
    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public int SortOrder { get; init; }
}
