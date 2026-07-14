using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Data.Dtos;

// ─── LLM Provider ────────────────────────────────────────────────

public record LlmProviderDto(
    int Id,
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    bool HasApiKey,        // 不回传明文 Key，仅告知是否已配置
    string? Description,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record LlmProviderDetailDto(
    int Id,
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    bool HasApiKey,
    string? Description,
    bool IsEnabled,
    LlmProviderQuotaDto? Quota,
    List<LlmModelDto> Models,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertLlmProviderRequest(
    string ProviderId,
    string Name,
    string Protocol,
    string BaseUrl,
    string? ApiKey,         // 传入时更新；null 表示不修改现有 Key
    string? Description,
    bool IsEnabled
);

// ─── LLM Provider Quota ──────────────────────────────────────────

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

// ─── LLM Model ───────────────────────────────────────────────────

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

// ─── TTS/ASR 语音服务商 ─────────────────────────────────────────

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

// ─── Agent Avatar（ADR-034）────────────────────────────────────────

public record AgentAvatarDto(
    string AvatarId,
    string Name,
    string Url,
    string? Personality,
    string? HairColor,
    string? Expression,
    List<string> VisualTraits,
    string? RecommendedUse,
    bool IsBuiltIn,
    bool IsEnabled,
    int SortOrder
);

// ─── Global Agent Template ────────────────────────────────────────

public record CapabilityDto(
    int Id,
    string CapabilityId,
    string Name,
    string? Description,
    string ToolName,
    string? ToolDescription,
    string? ToolParametersJson,
    bool RequiresShellExecution,
    bool RequiresFileWrite,
    bool RequiresNetworkAccess,
    bool IsEnabled,
    int SortOrder,
    string SourceKind,
    string? SourceId,
    string RuntimeStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertCapabilityRequest(
    string CapabilityId,
    string Name,
    string? Description,
    string ToolName,
    string? ToolDescription,
    string? ToolParametersJson,
    bool RequiresShellExecution,
    bool RequiresFileWrite,
    bool RequiresNetworkAccess,
    bool IsEnabled,
    int SortOrder
);

// ─── Skill Package ────────────────────────────────────────────────

public record SkillPackageDto(
    int Id,
    string SkillPackageId,
    string Name,
    string? Description,
    string Version,
    string FileName,
    long FileSizeBytes,
    bool IsEnabled,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpdateSkillPackageRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    int SortOrder
);

// ─── KeyVault ───────────────────────────────────────────────────

public record KeyVaultSecretDto(
    long Id,
    string KeyVaultId,
    string Name,
    string? Description,
    string Category,
    List<string> Tags,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record KeyVaultSecretDetailDto(
    long Id,
    string KeyVaultId,
    string Name,
    string? Description,
    string Category,
    List<string> Tags,
    string? Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateKeyVaultSecretRequest(
    string Name,
    string Value,
    string? Description,
    string Category,
    List<string>? Tags
);

public record UpdateKeyVaultSecretRequest(
    string Name,
    string? Value,
    string? Description,
    string Category,
    List<string>? Tags
);

public record KeyVaultTextTransformRequest(string Text);

public record KeyVaultTextTransformResponse(string Text);

public record GlobalAgentTemplateDto(
    int Id,
    string TemplateId,
    string Name,
    string? Description,
    string Role,
    string? SystemPrompt,
    string? UserPromptTemplate,
    string? PreferredProviderId,
    string? PreferredModelId,
    int MaxContextTokens,
    int MaxReplyTokens,
    string? ContainerImage,
    List<string> SelectedCapabilityIds,
    List<string> SelectedSkillPackageIds,
    bool IsBuiltIn,
    bool IsEnabled,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PersonaPrompt = null,
    string? ToolsDescription = null,
    string? BootstrapTemplate = null,
    string? AvatarEmoji = null,
    string? AvatarId = null,
    string? AvatarUrl = null,
    string? AvatarName = null,
    string? MemoryLlmProviderId = null,
    string? MemoryLlmModelId = null,
    string? MemorySearchMode = null,
    string? ReasoningEffort = null,
    int MaxRounds = 200,
    int MaxElapsedSeconds = 1200,
    int MaxToolCallsTotal = 100,
    string? ConsciousProfileId = null,
    string? SubconsciousProfileId = null,
    string? AgentsPrompt = null,
    string? MemoryPrompt = null,
    bool AllowFileWrite = false,
    bool AllowShellExecution = false,
    bool AllowNetworkAccess = false,
    List<string>? AllowedToolNames = null
);

public record UpsertGlobalAgentTemplateRequest(
    string TemplateId,
    string Name,
    string? Description,
    string Role,
    string? SystemPrompt,
    string? UserPromptTemplate,
    string? PreferredProviderId,
    string? PreferredModelId,
    int MaxContextTokens,
    int MaxReplyTokens,
    string? ContainerImage,
    List<string>? SelectedCapabilityIds,
    List<string>? SelectedSkillPackageIds,
    bool IsEnabled,
    int SortOrder,
    string? PersonaPrompt = null,
    string? ToolsDescription = null,
    string? BootstrapTemplate = null,
    string? AvatarEmoji = null,
    string? AvatarId = null,
    string? MemoryLlmProviderId = null,
    string? MemoryLlmModelId = null,
    string? MemorySearchMode = null,
    string? ReasoningEffort = null,
    int? MaxRounds = null,
    int? MaxElapsedSeconds = null,
    int? MaxToolCallsTotal = null,
    string? ConsciousProfileId = null,
    string? SubconsciousProfileId = null,
    string? AgentsPrompt = null,
    string? MemoryPrompt = null
);

// ─── Workspace Agent Template ─────────────────────────────────────

public record WorkspaceAgentTemplateDto(
    int Id,
    string WorkspaceId,
    string TemplateId,
    string Name,
    string? Description,
    string Role,
    string? SystemPrompt,
    string? UserPromptTemplate,
    string? PreferredProviderId,
    string? PreferredModelId,
    int MaxContextTokens,
    int MaxReplyTokens,
    string? ContainerImage,
    string? BaseGlobalTemplateId,
    List<string> SelectedCapabilityIds,
    bool IsEnabled,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PersonaPrompt = null,
    string? ToolsDescription = null,
    string? BootstrapTemplate = null,
    string? AvatarEmoji = null,
    string? AvatarId = null,
    string? AvatarUrl = null,
    string? AvatarName = null,
    string? MemoryLlmProviderId = null,
    string? MemoryLlmModelId = null,
    string? MemorySearchMode = null,
    string? ReasoningEffort = null
);

public record UpsertWorkspaceAgentTemplateRequest(
    string WorkspaceId,
    string TemplateId,
    string Name,
    string? Description,
    string Role,
    string? SystemPrompt,
    string? UserPromptTemplate,
    string? PreferredProviderId,
    string? PreferredModelId,
    int MaxContextTokens,
    int MaxReplyTokens,
    string? ContainerImage,
    string? BaseGlobalTemplateId,
    List<string>? SelectedCapabilityIds,
    bool IsEnabled,
    int SortOrder,
    string? PersonaPrompt = null,
    string? ToolsDescription = null,
    string? BootstrapTemplate = null,
    string? AvatarEmoji = null,
    string? AvatarId = null,
    string? MemoryLlmProviderId = null,
    string? MemoryLlmModelId = null,
    string? MemorySearchMode = null,
    string? ReasoningEffort = null
);

// ─── App User ─────────────────────────────────────────────────────

public record AppUserDto(
    int Id,
    string UserId,
    string Username,
    string Email,
    string? DisplayName,
    string UserType,
    bool IsEnabled,
    List<string> RoleIds,
    DateTimeOffset CreatedAt
);

public record CreateUserRequest(
    string UserId,
    string Username,
    string Email,
    string Password,
    string? DisplayName,
    string UserType   // "Admin" | "SimpleUser"
);

public record UpdateUserRequest(
    string Username,
    string Email,
    string? DisplayName,
    string UserType,
    bool IsEnabled
);

public record ChangePasswordRequest(string NewPassword);

public record AssignRolesRequest(List<string> RoleIds);

// ─── App Role ─────────────────────────────────────────────────────

public record AppRoleDto(
    int Id,
    string RoleId,
    string Name,
    string? Description,
    List<string> Permissions,
    bool IsSystemRole,
    DateTimeOffset CreatedAt
);

public record UpsertRoleRequest(
    string RoleId,
    string Name,
    string? Description,
    List<string> Permissions
);

// ─── Team ─────────────────────────────────────────────────────────

public record TeamDto(
    int Id,
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled,
    int MemberCount,
    int WorkspaceCount,
    DateTimeOffset CreatedAt
);

public record TeamDetailDto(
    int Id,
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    List<TeamMemberDto> Members,
    List<WorkspaceWithPermDto> Workspaces
);

public record UpsertTeamRequest(
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled
);

public record TeamMemberDto(
    string UserId,
    string Username,
    string? DisplayName,
    string Role   // "Member" | "Admin"
);

public record AddTeamMemberRequest(
    string UserId,
    string Role   // "Member" | "Admin"
);

// ─── Workspace ────────────────────────────────────────────────────

public record WorkspaceWithPermDto(
    int Id,
    string WorkspaceId,
    string Slug,
    string TeamId,
    string TeamName,
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    bool IsEnabled,
    bool IsFrozen,
    int MemberCount,
    DateTimeOffset CreatedAt,
    string? UserProfile = null
);

public record CreateWorkspaceRequest(
    string WorkspaceId,
    string TeamId,
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    string? UserProfile = null
);

public record UpdateWorkspaceRequest(
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    bool IsEnabled,
    string? UserProfile = null
);

public record WorkspaceMemberDto(
    int Id,
    string UserId,
    string Username,
    string? DisplayName,
    string AccessLevel
);

public record AddWorkspaceMemberRequest(
    string UserId,
    string AccessLevel  // "ReadOnly" | "Write" | "Manage"
);

// ── WorkspaceAgent ─────────────────────────────────────────────────

public record WorkspaceAgentDto(
    string AgentId,
    string Name,
    string? Description,
    string? DisplayName,
    string? AvatarId,
    string? AvatarUrl,
    string? SourceTemplateId,
    string? MainSessionId,
    string? SystemPromptOverride,
    string? PreferredProviderId,
    string? PreferredModelId,
    bool IsEnabled,
    bool IsFrozen,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? HeartbeatPrompt = null
);

public record CreateWorkspaceAgentRequest(
    string Name,
    string? Description,
    string? DisplayName,
    string? AvatarId,
    string? AvatarUrl,
    string? SourceTemplateId,
    string? SystemPromptOverride,
    string? PreferredProviderId,
    string? PreferredModelId,
    string? HeartbeatPrompt = null
);

public record UpdateWorkspaceAgentRequest(
    string Name,
    string? Description,
    string? DisplayName,
    string? AvatarId,
    string? AvatarUrl,
    string? SourceTemplateId,
    string? SystemPromptOverride,
    string? PreferredProviderId,
    string? PreferredModelId,
    bool IsEnabled,
    string? HeartbeatPrompt = null
);

// ── Workflow ────────────────────────────────────────────────────────

public record WorkflowDto(
    string WorkflowId,
    string Name,
    string? Description,
    string? DefinitionJson,
    string Status,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkflowRequest(
    string Name,
    string? Description,
    string? DefinitionJson,
    string Status,
    bool IsEnabled
);

// ── KnowledgeBase ───────────────────────────────────────────────────

public record KnowledgeBaseDto(
    string KbId,
    string Name,
    string? Description,
    string KbType,
    int DocumentCount,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertKnowledgeBaseRequest(
    string Name,
    string? Description,
    string KbType,
    bool IsEnabled
);

// ── WorkspaceSkill ──────────────────────────────────────────────────

public record WorkspaceSkillDto(
    string SkillId,
    string Name,
    string? Description,
    string SkillType,
    string? ConfigJson,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkspaceSkillRequest(
    string Name,
    string? Description,
    string SkillType,
    string? ConfigJson,
    bool IsEnabled
);

// ── WorkspaceChannel ────────────────────────────────────────────────

public record WorkspaceChannelDto(
    string ChannelId,
    string Name,
    string? Description,
    string ChannelType,
    string? DefaultAgentId,
    string? ConfigJson,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkspaceChannelRequest(
    string Name,
    string? Description,
    string ChannelType,
    string? DefaultAgentId,
    string? ConfigJson,
    bool IsEnabled
);

// ── Chat Proxy ──────────────────────────────────────────────────────

public record AdminChatRequest(
    string MessageText,
    string? OriginalMessageText,
    string? SessionId,
    string? AgentId,
    IReadOnlyList<string>? TargetAgentIds = null,
    string? Audience = null,
    bool SuppressUserTranscript = false,
    bool ForceNewSession = false,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? ClientRequestId = null
);

public record AdminChatResponse(
    string MessageId,
    string SessionId,
    string? Reply,
    bool IsSuccess,
    string? ErrorMessage,
    TokenUsageDto? Usage,
    IReadOnlyList<TurnStepDto>? TurnSteps
);

public record AdminChatSteeringRequest(
    string MessageText,
    string? AgentId = null,
    string? SourceQueueItemId = null,
    int Priority = 100
);

public record AdminChatSteeringResponse(
    string SteeringId,
    string SessionId,
    string WorkspaceId,
    string? AgentId,
    string Status,
    long CreatedAt
);

public record VisionArtifactUploadResponse(
    string ArtifactId,
    string MimeType,
    int? Width,
    int? Height,
    long CapturedAt
);

