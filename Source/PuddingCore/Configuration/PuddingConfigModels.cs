using System.Text.Json.Serialization;

namespace PuddingCode.Configuration;

/// <summary>
/// Provider compatibility settings for non-standard OpenAI-compatible APIs.
/// Stored in llm.providers.json under "compat" on a provider entry.
/// </summary>
public sealed record PuddingProviderCompatConfig
{
    /// <summary>Override the max tokens field name (e.g., "max_tokens" for Kimi K3). Default: "max_completion_tokens".</summary>
    public string MaxTokensField { get; init; } = "max_completion_tokens";

    /// <summary>Message content must be a plain string, not an array of content parts.</summary>
    public bool RequiresStringContent { get; init; }

    /// <summary>Use top-level "reasoning_effort" instead of nested "thinking" object.</summary>
    public bool UseReasoningEffort { get; init; }

    /// <summary>Default reasoning effort when UseReasoningEffort=true (K3: "max").</summary>
    public string? DefaultReasoningEffort { get; init; }

    /// <summary>Streaming responses include usage statistics. Default: true.</summary>
    public bool SupportsUsageInStreaming { get; init; } = true;

    /// <summary>Require reasoning_content field in assistant messages with tool_calls.</summary>
    public bool RequiresReasoningContentInToolMessages { get; init; }
}

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
    public PuddingDesktopConfig Desktop { get; init; } = new();
}

public sealed record PuddingDesktopConfig
{
    public PuddingDesktopCoreConfig Core { get; init; } = new();
    public PuddingDesktopBootstrapConfig Bootstrap { get; init; } = new();
}

public sealed record PuddingDesktopCoreConfig
{
    public bool AutoStart { get; init; } = true;
    public bool AutoRestart { get; init; } = true;
    public int RestartMaxAttempts { get; init; } = 3;
    public int RestartWindowSeconds { get; init; } = 60;
    public int RestartInitialDelaySeconds { get; init; } = 2;
    public int RestartMaxDelaySeconds { get; init; } = 30;
    public int Port { get; init; }
    public int StartupTimeoutSeconds { get; init; } = 60;
    public int ShutdownTimeoutSeconds { get; init; } = 15;
    public string? ControlToken { get; init; }
}

/// <summary>
/// Desktop guided-bootstrap configuration (system.json → desktop.bootstrap).
/// The Desktop exposes a loopback HTTP control endpoint (default on) plus an
/// opt-in signal-file polling loop. On a valid "rebuild-restart" trigger it
/// stops Core, runs an incremental dotnet build, optionally writes yolo.signal,
/// copies the build output into the Desktop run directory and restarts Core.
/// All paths are dynamic; nothing is hardcoded at runtime.
/// </summary>
public sealed record PuddingDesktopBootstrapConfig
{
    /// <summary>Master switch for the signal-file polling loop. Defaults to false — polling is opt-in; the HTTP endpoint is the primary trigger.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>When true (default), starts the loopback-only HTTP control endpoint (POST /desktop/bootstrap/start, GET /desktop/bootstrap/status).</summary>
    public bool HttpEnabled { get; init; } = true;

    /// <summary>Loopback-only HTTP port for the control endpoint. Default 8199, deliberately away from the Core HTTP port (8080). Only 127.0.0.1 is bound — never a LAN interface.</summary>
    public int HttpPort { get; init; } = 8199;

    /// <summary>
    /// When true, copies the build output into the Desktop run directory after
    /// a successful build so Core starts side-by-side. Defaults to false — the
    /// primary model is CoreExecutablePath pointing directly at the PuddingAgent
    /// Debug output directory; output sync is only an optional compatibility
    /// shim for the legacy layout.
    /// </summary>
    public bool SyncBuildOutput { get; init; } = false;

    /// <summary>Absolute signal file path. When null/empty defaults to &lt;DataRoot&gt;\config\rebuild.signal.</summary>
    public string? SignalPath { get; init; }

    /// <summary>Project to build, relative to the repository root. Default matches dev-up.py backend_build_command.</summary>
    public string BuildProjectRelativePath { get; init; } = "Source/PuddingAgent/PuddingAgent.csproj";

    /// <summary>
    /// Absolute path of the csproj to build. When set, it takes precedence over
    /// BuildProjectRelativePath (absolute path prevents accidental re-routing).
    /// </summary>
    public string? BuildProjectPath { get; init; }

    /// <summary>Extra arguments appended to "dotnet build &lt;project&gt;". Empty by default.</summary>
    public string BuildArguments { get; init; } = "";

    /// <summary>When true, write yolo.signal after a successful build (Core enters YOLO mode on restart).</summary>
    public bool AutoYolo { get; init; } = true;

    /// <summary>Build timeout in seconds. Default 300.</summary>
    public int BuildTimeoutSeconds { get; init; } = 300;
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
    public List<PuddingLlmProviderConfig> Providers { get; init; } = [];
    public Dictionary<string, PuddingLlmProfileConfig> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public PuddingLlmRoleConfig Roles { get; init; } = new();
    public PuddingLlmImageGenerationConfig? ImageGeneration { get; init; }
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
    public string? Description { get; init; }
    public List<PuddingLlmModelConfig> Models { get; init; } = [];

    // ── 服务商额度元数据 ────────────────────────────────
    /// <summary>服务商级最大并发请求数（null=使用内置默认值 50）</summary>
    public int? MaxConcurrentRequests { get; init; }
    /// <summary>服务商声明的每分钟 Token 配额（TPM）</summary>
    public long? TokensPerMinute { get; init; }
    /// <summary>服务商声明的每分钟请求配额（RPM）</summary>
    public int? RequestsPerMinute { get; init; }

    // ── 超时策略 ──────────────────────────────────────
    /// <summary>非流式请求超时秒数（默认 240）</summary>
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

    /// <summary>Provider compatibility settings for non-standard APIs (e.g., Kimi K3).</summary>
    public PuddingProviderCompatConfig? Compat { get; init; }
}

public sealed record PuddingLlmModelConfig
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public int? MaxContextTokens { get; init; }
    public int? MaxInputTokens { get; init; }
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

/// <summary>
/// [Obsolete] LLM 角色→Profile 映射。主 Agent 应直接在自己的 manifest.json
/// 中指定 preferredProviderId/preferredModelId，不再通过全局资源池的 profiles/roles 间接寻址。
/// 保留该类型仅为兼容已有配置文件的反序列化，新配置应整体留空。
/// </summary>
[Obsolete("Agent should define preferredProviderId/preferredModelId directly in manifest.json. Global roles are no longer required.")]
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

/// <summary>Default provider/model binding for image generation tools.</summary>
public sealed record PuddingLlmImageGenerationConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
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
    public int MaxElapsedSeconds { get; init; } = 86400;
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

/// <summary>
/// Agent 实例 manifest — 创建时从模板复制全部配置，运行时自包含，不再跨目录引用模板。
/// </summary>
public sealed record AgentInstanceManifest
{
    // ── 实例身份字段 ──
    public string AgentInstanceId { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? AvatarId { get; init; }
    public string? AvatarUrl { get; init; }
    public string? MainSessionId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsFrozen { get; init; }
    public AgentInstancePaths Paths { get; init; } = new();
    /// <summary>
    /// Workspace channel instance references. Channel credentials and provider
    /// settings live under data/channels and never belong to the Agent manifest.
    /// </summary>
    public List<string> ChannelIds { get; init; } = [];
    /// <summary>
    /// One-time migration source for pre channel-catalog development data.
    /// New writes clear this value after moving the binding to data/channels.
    /// </summary>
    [Obsolete("Use ChannelIds and ChannelInstanceManifest instead.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentFeishuBotConfig? Feishu { get; init; }

    // ── 模板配置（创建时嵌入，运行时不再查模板）──
    public string? Role { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPromptTemplate { get; init; }
    public string? MemorySearchMode { get; init; }
    public string? ReasoningEffort { get; init; }
    public int MaxReplyTokens { get; init; } = 4096;
    public int MaxRounds { get; init; } = 200;
    public int MaxElapsedSeconds { get; init; } = 86400;
    public int MaxToolCallsTotal { get; init; } = 100;
    public string? ContainerImage { get; init; }
    public AgentDefaultLlmProfiles DefaultLlmProfiles { get; init; } = new();
    public AgentCapabilitiesConfig Capabilities { get; init; } = new();
    public List<string> SkillPackageIds { get; init; } = [];
    public string? PreferredProviderId { get; init; }
    public string? PreferredModelId { get; init; }
    public string? MemoryLlmProviderId { get; init; }
    public string? MemoryLlmModelId { get; init; }
    public string? EmbeddingProviderId { get; init; }
    public string? EmbeddingModelId { get; init; }

    // ── Smart 工具模型配置（子代理角色→模型映射）──
    public string? ExplorerModel { get; init; }
    public string? ResearcherModel { get; init; }
    public string? PlannerModel { get; init; }
    public string? ReviewerModel { get; init; }
    public string? DeveloperModel { get; init; }
    public string? DeployerModel { get; init; }
    public string? TesterModel { get; init; }

    // ── Markdown 文件引用（相对于实例根目录的文件名，如 "SOUL.md"）──
    public string? SoulMdFile { get; init; }
    public string? AgentsMdFile { get; init; }
    public string? ToolsMdFile { get; init; }
    public string? BootstrapMdFile { get; init; }
    public string? MemoryMdFile { get; init; }
    public string? HeartbeatMdFile { get; init; }
}

/// <summary>
/// Agent 级飞书机器人配置。V1 中飞书是唯一的第三方聊天渠道；
/// 后续渠道扩展不得改变本配置作为 Agent 私有配置的事实。
/// </summary>
public sealed record AgentFeishuBotConfig
{
    public bool Enabled { get; init; }
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>
    /// Project committed Agent deltas to a CardKit streaming reply. When CardKit
    /// is unavailable, the terminal reply falls back to the normal text delivery.
    /// </summary>
    public bool StreamingRepliesEnabled { get; init; } = true;
    /// <summary>
    /// Allows explicit voice output through Markdown voice fences or send_voice.
    /// Plain successful replies remain text-only.
    /// </summary>
    public bool TtsRepliesEnabled { get; init; }
    public string TtsVoice { get; init; } = "Cherry";
    /// <summary>
    /// Feishu sender open_ids allowed to execute privileged Pudding commands
    /// through this Agent-owned bot. Regular chat is not restricted by this list.
    /// </summary>
    public IReadOnlyList<string> PrivilegedUserOpenIds { get; init; } = [];
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
