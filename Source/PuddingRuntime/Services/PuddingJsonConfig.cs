using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingRuntime.Services;

/// <summary>
/// JSON 配置文件模型 —— 作为 LLM 服务商、Agent 模板、工作区、Agent 实例的唯一配置来源。
/// 文件路径：data/conf/pudding-config.json，Docker 挂载后可在宿主机直接编辑。
/// SQLite 仅存储业务数据（聊天记录、记忆、会话等）。
/// </summary>

// ── 顶层结构 ────────────────────────────────────────

public sealed class PuddingJsonConfig
{
    [JsonPropertyName("llm")]
    public PuddingLlmConfig? Llm { get; set; }

    [JsonPropertyName("providers")]
    public List<JsonProvider>? Providers { get; set; }

    [JsonPropertyName("agentTemplates")]
    public List<JsonAgentTemplate>? AgentTemplates { get; set; }

    [JsonPropertyName("workspaces")]
    public List<JsonWorkspace>? Workspaces { get; set; }
}

// ── LLM ──────────────────────────────────────────────

public sealed class PuddingLlmConfig
{
    [JsonPropertyName("conscious")]
    public PuddingLlmEntry? Conscious { get; set; }

    [JsonPropertyName("memory")]
    public PuddingLlmEntry? Memory { get; set; }
}

public sealed class PuddingLlmEntry
{
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("searchMode")]
    public string? SearchMode { get; set; }

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("thinkingMode")]
    public string? ThinkingMode { get; set; }
}

// ── Provider & Model ─────────────────────────────────

public sealed class JsonProvider
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "openai";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("models")]
    public List<JsonProviderModel>? Models { get; set; }
}

public sealed class JsonProviderModel
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("maxContextTokens")]
    public int MaxContextTokens { get; set; } = 8192;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; } = 2048;

    [JsonPropertyName("capabilityTags")]
    public List<string>? CapabilityTags { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

// ── Agent Template ───────────────────────────────────

public sealed class JsonAgentTemplate
{
    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "Service";

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("personaPrompt")]
    public string? PersonaPrompt { get; set; }

    [JsonPropertyName("avatarEmoji")]
    public string? AvatarEmoji { get; set; }

    [JsonPropertyName("preferredProviderId")]
    public string? PreferredProviderId { get; set; }

    [JsonPropertyName("preferredModelId")]
    public string? PreferredModelId { get; set; }

    [JsonPropertyName("memorySearchMode")]
    public string MemorySearchMode { get; set; } = "deep";

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("maxContextTokens")]
    public int MaxContextTokens { get; set; } = 65536;

    [JsonPropertyName("maxReplyTokens")]
    public int MaxReplyTokens { get; set; } = 16384;

    [JsonPropertyName("isBuiltIn")]
    public bool IsBuiltIn { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

// ── Workspace & Agent ────────────────────────────────

public sealed class JsonWorkspace
{
    [JsonPropertyName("workspaceId")]
    public string WorkspaceId { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("agents")]
    public List<JsonAgent>? Agents { get; set; }
}

public sealed class JsonAgent
{
    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sourceTemplateId")]
    public string? SourceTemplateId { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}

// ── Source-generated JSON serializer context ─────────

[JsonSerializable(typeof(PuddingJsonConfig))]
internal partial class PuddingJsonConfigContext : JsonSerializerContext
{
}
