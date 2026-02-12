using System.Text.Json.Serialization;

namespace PuddingCode.Configuration;

/// <summary>
/// 结构化配置加载结果 — 包含成功标志、配置数据、验证错误列表。
/// 验证错误不抛异常，由调用方决定如何处理。
/// </summary>
public sealed record ConfigLoadResult<T>
{
    public bool Success { get; init; }
    public T? Config { get; init; }
    public List<string> Errors { get; init; } = [];

    public static ConfigLoadResult<T> Ok(T config) => new() { Success = true, Config = config };

    public static ConfigLoadResult<T> Fail(List<string> errors) => new() { Success = false, Errors = errors };

    public static ConfigLoadResult<T> Fail(string error) => new() { Success = false, Errors = [error] };
}

public sealed record PuddingSystemConfig
{
    public string Environment { get; init; } = "production";
    public PuddingHttpConfig Http { get; init; } = new();
    public PuddingLoggingConfig Logging { get; init; } = new();
    public PuddingRuntimeConfig Runtime { get; init; } = new();
    public PuddingPathConfig Paths { get; init; } = new();
}

public sealed record PuddingHttpConfig
{
    public int Port { get; init; } = 8080;
    public string? PublicBaseUrl { get; init; }
}

public sealed record PuddingLoggingConfig
{
    public string Level { get; init; } = "Information";
    public bool StructuredJson { get; init; }
}

public sealed record PuddingRuntimeConfig
{
    public int MaxAgentRounds { get; init; } = 200;
    public bool EnableRuntimeDiagnostics { get; init; } = true;
    public bool EnableFrontendDebug { get; init; }
    public bool EnableFakeLlm { get; init; }
    public PuddingFuseConfig? Fuse { get; init; }
}

/// <summary>Session-level sliding-window fuse configuration.</summary>
public sealed record PuddingFuseConfig
{
    /// <summary>Maximum errors within the sliding window before fuse triggers (default 10).</summary>
    public int MaxErrorsInWindow { get; init; } = 10;

    /// <summary>Error count at which warnings begin (default 5).</summary>
    public int WarningThreshold { get; init; } = 5;

    /// <summary>Sliding window duration in seconds (default 60).</summary>
    public int WindowSeconds { get; init; } = 60;
}

public sealed record PuddingPathConfig
{
    public string? DataRoot { get; init; }
}

public sealed record PuddingLlmProvidersConfig
{
    public string? DefaultProviderId { get; init; }
    public string? DefaultModelId { get; init; }
    public List<PuddingLlmProviderConfig> Providers { get; init; } = [];
    public Dictionary<string, PuddingLlmProfileConfig> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public PuddingLlmRoleConfig Roles { get; init; } = new();
    public PuddingLlmEmbeddingConfig? Embedding { get; init; }
    public PuddingVoiceProvidersConfig? Voice { get; init; }
}

public sealed record PuddingLlmProviderConfig
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "openai";
    public string BaseUrl { get; init; } = "";
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }
    public string? ApiKeyRef { get; init; }
    public bool IsEnabled { get; init; } = true;
    public List<PuddingLlmModelConfig> Models { get; init; } = [];

    // ── 超时策略 ──────────────────────────────────────
    /// <summary>非流式请求超时秒数（默认 120）</summary>
    public int? RequestTimeoutSeconds { get; init; }
    /// <summary>流式请求超时秒数（默认 300）</summary>
    public int? StreamTimeoutSeconds { get; init; }

    // ── 重试策略 ──────────────────────────────────────
    /// <summary>最大重试次数（默认 2，仅对瞬态错误）</summary>
    public int? MaxRetries { get; init; }
    /// <summary>重试间隔秒数（默认 1）</summary>
    public int? RetryDelaySeconds { get; init; }

    // ── 熔断策略 ──────────────────────────────────────
    /// <summary>连续失败阈值（默认 5，达到后熔断）</summary>
    public int? CircuitBreakerFailureThreshold { get; init; }
    /// <summary>熔断恢复等待秒数（默认 60）</summary>
    public int? CircuitBreakerRecoverySeconds { get; init; }
}

public sealed record PuddingLlmModelConfig
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public int? MaxContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public List<string> CapabilityTags { get; init; } = [];
    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public int SortOrder { get; init; }
    public string? ReasoningEffort { get; init; }
    public bool IsEmbedding { get; init; }
    public decimal? PricePer1MInputTokens { get; init; }
    public decimal? PricePer1MOutputTokens { get; init; }
    public decimal? PricePer1MCacheHitTokens { get; init; }
    /// <summary>模型级最大并发请求数（null=继承 Provider 默认 50）</summary>
    public int? MaxConcurrentRequests { get; init; }
}

public sealed record PuddingLlmProfileConfig
{
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? ReasoningEffort { get; init; }
    public string? ThinkingMode { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? MaxReplyTokens { get; init; }
    public string? SystemPrompt { get; init; }
    public float? Temperature { get; init; }
}

public sealed record PuddingLlmRoleConfig
{
    public string? Conscious { get; init; }
    public string? Subconscious { get; init; }
}

public sealed record PuddingSecurityConfig
{
    public PuddingJwtConfig Jwt { get; init; } = new();
    public PuddingKeyVaultConfig KeyVault { get; init; } = new();
}

public sealed record PuddingJwtConfig
{
    public string Issuer { get; init; } = "pudding-platform";
    public string Audience { get; init; } = "pudding-admin";
    public int ExpiryHours { get; init; } = 8;
    public string? Key { get; init; }
}

public sealed record PuddingKeyVaultConfig
{
    public string Mode { get; init; } = "local-file";
    public string? MasterKeyRef { get; init; }
}

public sealed record PuddingConnectorsConfig
{
    public PuddingConnectorConfig Http { get; init; } = new();
    public PuddingConnectorConfig Websocket { get; init; } = new();
    public PuddingConnectorConfig Mqtt { get; init; } = new();
    public PuddingP2pConfig P2p { get; init; } = new();
}

public sealed record PuddingConnectorConfig
{
    public bool Enabled { get; init; } = true;
}

public sealed record PuddingP2pConfig
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 9527;
}

/// <summary>Embedding 服务全局默认配置。</summary>
public sealed record PuddingLlmEmbeddingConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public int? Dimension { get; init; }
}

public sealed record AgentTemplateManifest
{
    public string TemplateId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Role { get; init; } = "Service";
    public AgentDefaultLlmProfiles DefaultLlmProfiles { get; init; } = new();
    public string MemorySearchMode { get; init; } = "deep";
    public string? ReasoningEffort { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPromptTemplate { get; init; }
    public int MaxContextTokens { get; init; } = 65536;
    public int MaxReplyTokens { get; init; } = 4096;
    public int MaxRounds { get; init; } = 200;
    public int MaxElapsedSeconds { get; init; } = 1200;
    public int MaxToolCallsTotal { get; init; } = 100;
    public string? ContainerImage { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public AgentCapabilitiesConfig Capabilities { get; init; } = new();
    public List<string> SkillPackageIds { get; init; } = [];
    public string? AvatarId { get; init; }
    public string? PreferredProviderId { get; init; }
    public string? PreferredModelId { get; init; }
    public string? MemoryLlmProviderId { get; init; }
    public string? MemoryLlmModelId { get; init; }
    public string? EmbeddingProviderId { get; init; }
    public string? EmbeddingModelId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AgentDefaultLlmProfiles
{
    public string? Conscious { get; init; }
    public string? Subconscious { get; init; }
}

public sealed record AgentCapabilitiesConfig
{
    public bool AllowTools { get; init; } = true;
    public List<string> AllowedToolIds { get; init; } = [];
    public bool AllowFileWrite { get; init; }
    public bool AllowShellExecution { get; init; }
    public bool AllowNetworkAccess { get; init; }
    public List<string> AllowedToolNames { get; init; } = [];
}

public sealed record AgentInstanceManifest
{
    public string AgentInstanceId { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? AvatarId { get; init; }
    public string? AvatarUrl { get; init; }
    public string? MainSessionId { get; init; }
    public string? HeartbeatPrompt { get; init; }
    public bool IsEnabled { get; init; } = true;
    public AgentInstancePaths Paths { get; init; } = new();
}

public sealed record AgentInstancePaths
{
    public string Config { get; init; } = "config";
    public string Workspace { get; init; } = "workspace";
    public string State { get; init; } = "state";
    public string Logs { get; init; } = "logs";
}

public sealed record AgentInstanceLlmConfig
{
    public AgentLlmBinding? Conscious { get; init; }
    public AgentLlmBinding? Subconscious { get; init; }
}

public sealed record AgentLlmBinding
{
    public string? ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ThinkingMode { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? MaxReplyTokens { get; init; }
}

public sealed record WorkspaceAgentRef
{
    public string AgentInstanceId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string AgentPath { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
}

// ── TTS/ASR 语音服务商配置 ──────────────────────────────────────

/// <summary>语音服务商根配置，存储于 config/voice/providers.json。</summary>
public sealed record PuddingVoiceProvidersConfig
{
    public List<PuddingVoiceProviderConfig> Providers { get; init; } = [];
    public string? DefaultTtsProviderId { get; init; }
    public string? DefaultTtsModelId { get; init; }
    public string? DefaultAsrProviderId { get; init; }
    public string? DefaultAsrModelId { get; init; }
}

public sealed record PuddingVoiceProviderConfig
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public string? Description { get; init; }
    public List<PuddingTtsModelConfig> TtsModels { get; init; } = [];
    public List<PuddingAsrModelConfig> AsrModels { get; init; } = [];
}

public sealed record PuddingTtsModelConfig
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public List<string> Voices { get; init; } = [];
    public List<string> AudioFormats { get; init; } = [];
    public List<int> SampleRates { get; init; } = [];
    public bool SupportsStreaming { get; init; }
    public bool SupportsInstructions { get; init; }
    public bool SupportsVoiceCloning { get; init; }
    public bool SupportsVoiceDesign { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsDefault { get; init; }
    public int SortOrder { get; init; }
}

public sealed record PuddingAsrModelConfig
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public List<string> Languages { get; init; } = [];
    public List<int> SampleRates { get; init; } = [];
    public bool SupportsEmotion { get; init; }
    public bool SupportsTimestamps { get; init; }
    public bool SupportsHotWords { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsDefault { get; init; }
    public int SortOrder { get; init; }
}

// ── TTS 前端请求 ────────────────────────────────────────────────

/// <summary>前端 TTS 合成请求，不暴露提供者密钥。</summary>
public sealed record TtsSynthesizeRequest
{
    public required string Text { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Voice { get; init; }
    public string? Format { get; init; }
    public int SampleRate { get; init; }
    public string? Instructions { get; init; }
}
