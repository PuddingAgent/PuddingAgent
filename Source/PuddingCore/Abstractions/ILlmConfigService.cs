using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 统一 LLM 配置服务 — 所有 LLM 服务商、模型、密钥的唯一数据入口。
/// 
/// 设计原则：
///   · 单一来源：所有 LLM 配置从 data/config/llm.providers.json 读取
///   · 启动时加载一次，运行时不热重载
///   · DB 之前存储的 LLM 配置表（LlmProviderEntity 等）已不再作为配置来源
/// </summary>
public interface ILlmConfigService
{
    IReadOnlyList<LlmProviderInfo> GetEnabledProviders();
    IReadOnlyList<LlmModelInfo> GetAllModels();
    LlmConfig? Resolve(string providerId, string? modelId = null);
    LlmProfileInfo? ResolveProfile(string profileId);
    /// <summary>返回配置源显式解析的默认 Provider/Profile/Model 与调用配置。</summary>
    LlmProfileInfo GetDefaultProfile();
    LlmConfig GetDefault();
    LlmConfig? GetMemoryConfig();
    LlmConfig? GetEmbeddingConfig();
    LlmProviderStrategy? GetProviderStrategy(string providerId);

    /// <summary>
    /// 根据 providerId + modelId 获取模型级别的并发/重试策略。
    /// 模型级配置优先于 Provider 级；模型未单独配置时回退到 Provider 策略。
    /// </summary>
    LlmProviderStrategy? GetModelStrategy(string providerId, string modelId);

    /// <summary>
    /// 从配置对象重新加载内存缓存（热更新）。
    /// </summary>
    void Reload(object config);
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
    public decimal InputPricePer1MTokens { get; init; }
    public decimal OutputPricePer1MTokens { get; init; }
    public decimal CacheHitPricePer1MTokens { get; init; }
    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsEmbedding { get; init; }
        public int SortOrder { get; init; }
    public List<string> CapabilityTags { get; init; } = [];
    public int? MaxConcurrentRequests { get; init; }
}

/// <summary>显式 LLM profile 解析结果。</summary>
public sealed record LlmProfileInfo
{
    public required string ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required LlmConfig Config { get; init; }
}

/// <summary>
/// LLM Provider 超时/重试/熔断策略配置。
/// 所有字段均可为 null，null 时使用内置默认值。
/// </summary>
public sealed record LlmProviderStrategy
{
    public static readonly LlmProviderStrategy Default = new();

    public int? RequestTimeoutSeconds { get; init; }
    public int? StreamTimeoutSeconds { get; init; }
    public int? MaxRetries { get; init; }
    public int? RetryDelaySeconds { get; init; }
    public int? CircuitBreakerFailureThreshold { get; init; }
    public int? CircuitBreakerRecoverySeconds { get; init; }
    public int? MaxConcurrentRequests { get; init; }
    public long? TokensPerMinute { get; init; }
    public int? RequestsPerMinute { get; init; }

    public int EffectiveRequestTimeoutSeconds => RequestTimeoutSeconds ?? 240;
    public int EffectiveMaxConcurrentRequests => MaxConcurrentRequests ?? 50;
    public int EffectiveStreamTimeoutSeconds => StreamTimeoutSeconds ?? 300;
    public int EffectiveMaxRetries => MaxRetries ?? 2;
    public int EffectiveRetryDelaySeconds => RetryDelaySeconds ?? 1;
    public int EffectiveCircuitBreakerFailureThreshold => CircuitBreakerFailureThreshold ?? 5;
    public int EffectiveCircuitBreakerRecoverySeconds => CircuitBreakerRecoverySeconds ?? 60;
}
