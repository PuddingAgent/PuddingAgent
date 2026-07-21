namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// LLM / Voice Provider DTOs — AI 服务商配置、模型、配额。
// ════════════════════════════════════════════════════════════════

// ── LLM Provider 服务商 ────────────────────────────────────────

/// <summary>LLM 服务商列表项（不含明文 Key）。</summary>
public record LlmProviderDto(
    int Id,
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    bool HasApiKey,
    string? Description,
    bool IsEnabled,
    int? MaxConcurrentRequests,
    long? TokensPerMinute,
    int? RequestsPerMinute,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>LLM 服务商详情（含 Quota + Models）。</summary>
public record LlmProviderDetailDto(
    int Id,
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    bool HasApiKey,
    string? Description,
    bool IsEnabled,
    int? MaxConcurrentRequests,
    long? TokensPerMinute,
    int? RequestsPerMinute,
    LlmProviderQuotaDto? Quota,
    List<LlmModelDto> Models,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>Provider 兼容性配置请求（K3 等）。</summary>
public record ProviderCompatRequest(
    string? MaxTokensField = null,
    bool RequiresStringContent = false,
    bool UseReasoningEffort = false,
    string? DefaultReasoningEffort = null,
    bool SupportsUsageInStreaming = true,
    bool RequiresReasoningContentInToolMessages = false
);

/// <summary>创建/更新 LLM 服务商请求。</summary>
public record UpsertLlmProviderRequest(
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    string? ApiKey,
    string? Description,
    bool IsEnabled,
    int? MaxConcurrentRequests = null,
    long? TokensPerMinute = null,
    int? RequestsPerMinute = null,
    int? RequestTimeoutSeconds = null,
    int? StreamTimeoutSeconds = null,
    ProviderCompatRequest? Compat = null
);

// ── LLM Provider Quota 配额 ────────────────────────────────────

public record LlmProviderQuotaDto(
    long? DailyTokenLimit,
    long? MonthlyTokenLimit,
    long DailyTokensUsed,
    long MonthlyTokensUsed,
    bool IsSuspended,
    DateTimeOffset? DailyResetAt,
    DateTimeOffset? MonthlyResetAt,
    DateTimeOffset UpdatedAt
);

public record UpdateQuotaRequest(
    long? DailyTokenLimit,
    long? MonthlyTokenLimit
);

// ── LLM Model 模型 ─────────────────────────────────────────────

/// <summary>LLM 模型 DTO（含定价和能力标签）。</summary>
public record LlmModelDto(
    int Id,
    int ProviderId,
    string ModelId,
    string Name,
    string? Description,
    int MaxContextTokens,
    int MaxOutputTokens,
    decimal InputPricePer1MTokens,
    decimal OutputPricePer1MTokens,
    decimal CacheHitPricePer1MTokens,
    List<string> CapabilityTags,
    bool IsDeprecated,
    bool IsDefault,
    bool IsEmbedding,
    int SortOrder,
    int? MaxConcurrentRequests,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertLlmModelRequest(
    string ModelId,
    string Name,
    string? Description,
    int MaxContextTokens,
    int MaxOutputTokens,
    decimal InputPricePer1MTokens,
    decimal OutputPricePer1MTokens,
    decimal CacheHitPricePer1MTokens,
    List<string>? CapabilityTags,
    bool IsDeprecated,
    bool IsDefault,
    bool IsEmbedding,
    int SortOrder,
    int? MaxConcurrentRequests
);

// ── TTS/ASR 语音服务商 ────────────────────────────────────────

public record VoiceProviderDto(
    string ProviderId,
    string Name,
    string Endpoint,
    bool HasApiKey,
    string? Description,
    bool IsEnabled,
    int TtsModelCount,
    int AsrModelCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record VoiceProviderDetailDto(
    string ProviderId,
    string Name,
    string Endpoint,
    bool HasApiKey,
    string? Description,
    bool IsEnabled,
    List<TtsModelDto> TtsModels,
    List<AsrModelDto> AsrModels,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertVoiceProviderRequest(
    string ProviderId,
    string Name,
    string Endpoint,
    string? ApiKey,
    string? Description,
    bool IsEnabled
);

public record TtsModelDto(
    string ModelId,
    string Name,
    string? Path,
    List<string> Voices,
    List<string> AudioFormats,
    List<int> SampleRates,
    bool SupportsStreaming,
    bool SupportsInstructions,
    bool SupportsVoiceCloning,
    bool SupportsVoiceDesign,
    bool IsDeprecated,
    bool IsDefault,
    int SortOrder
);

public record UpsertTtsModelRequest(
    string ModelId,
    string Name,
    string? Path,
    List<string>? Voices,
    List<string>? AudioFormats,
    List<int>? SampleRates,
    bool SupportsStreaming,
    bool SupportsInstructions,
    bool SupportsVoiceCloning,
    bool SupportsVoiceDesign,
    bool IsDeprecated,
    bool IsDefault,
    int SortOrder
);

public record AsrModelDto(
    string ModelId,
    string Name,
    string? Path,
    List<string> Languages,
    List<int> SampleRates,
    bool SupportsEmotion,
    bool SupportsTimestamps,
    bool SupportsHotWords,
    bool IsDeprecated,
    bool IsDefault,
    int SortOrder
);

public record UpsertAsrModelRequest(
    string ModelId,
    string Name,
    string? Path,
    List<string>? Languages,
    List<int>? SampleRates,
    bool SupportsEmotion,
    bool SupportsTimestamps,
    bool SupportsHotWords,
    bool IsDeprecated,
    bool IsDefault,
    int SortOrder
);
