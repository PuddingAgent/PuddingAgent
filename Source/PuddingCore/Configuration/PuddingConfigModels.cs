using System.Text.Json.Serialization;

namespace PuddingCode.Configuration;

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
}

public sealed record PuddingLlmModelConfig
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public int MaxContextTokens { get; init; }
    public int MaxOutputTokens { get; init; }
    public List<string> CapabilityTags { get; init; } = [];
    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public int SortOrder { get; init; }
}

public sealed record PuddingLlmProfileConfig
{
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? ReasoningEffort { get; init; }
    public string? ThinkingMode { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? MaxReplyTokens { get; init; }
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

public sealed record AgentTemplateManifest
{
    public string TemplateId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Role { get; init; } = "Service";
    public AgentDefaultLlmProfiles DefaultLlmProfiles { get; init; } = new();
    public string MemorySearchMode { get; init; } = "deep";
    public string? ReasoningEffort { get; init; }
    public int MaxContextTokens { get; init; } = 65536;
    public int MaxReplyTokens { get; init; } = 4096;
    public bool IsBuiltIn { get; init; }
    public bool IsEnabled { get; init; } = true;
    public AgentCapabilitiesConfig Capabilities { get; init; } = new();
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
}

public sealed record AgentInstanceManifest
{
    public string AgentInstanceId { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
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
