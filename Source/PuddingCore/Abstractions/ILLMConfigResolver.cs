using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// LLM 配置解析器：从 Agent 实例 LLM binding + data/config/llm.providers.json 解析路由。
/// 与 IAgentTemplateProvider 分离：后者负责个性/提示词，本接口负责 LLM 基础设施配置。
/// </summary>
public interface ILLMConfigResolver
{
    /// <summary>
    /// Resolve one semantic LLM role from the persistent Agent instance that owns
    /// configuration for the execution. Callers choose a role; they never choose
    /// a provider/model fallback in code.
    /// </summary>
    Task<AgentRoleLlmRoutingConfig> ResolveRoleAsync(
        string workspaceId,
        string configurationAgentInstanceId,
        string roleId,
        CancellationToken ct = default);

    /// <summary>
    /// 从 Agent 实例的 LLM binding 解析路由：根据 providerId/modelId/profileId
    /// 从 llm.providers.json 补齐 endpoint/key。不再依赖模板文件。
    /// </summary>
    Task<LlmRoutingConfig?> ResolveAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default);

    /// <summary>
    /// 从 Agent 实例的 LLM binding 解析记忆 LLM 路由。
    /// </summary>
    Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default);
}

/// <summary>Stable semantic LLM role identifiers.</summary>
public static class AgentLlmRoleIds
{
    public const string Conscious = "conscious";
    public const string Subconscious = "subconscious";
    public const string Explorer = "explorer";
    public const string Researcher = "researcher";
    public const string Planner = "planner";
    public const string Reviewer = "reviewer";
    public const string Developer = "developer";
    public const string Deployer = "deployer";
    public const string Tester = "tester";
}

/// <summary>
/// Immutable route snapshot for one Agent semantic role. Provider/model identity
/// comes from the Agent instance; endpoint and credentials come from the provider registry.
/// </summary>
public sealed record AgentRoleLlmRoutingConfig
{
    public required string RoleId { get; init; }
    public required string ConfigurationAgentInstanceId { get; init; }
    public required string ProviderId { get; init; }
    public required string ProfileId { get; init; }
    public required string ModelId { get; init; }
    public required LlmConfig Config { get; init; }
    public string SearchMode { get; init; } = "deep";
}

/// <summary>显意识 LLM 路由配置。</summary>
public sealed record LlmRoutingConfig
{
    public string? ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public LlmConfig? Config { get; init; }
}

/// <summary>潜意识 LLM 路由配置。</summary>
public sealed record MemoryLlmRoutingConfig
{
    public string? ProviderId { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
    public string SearchMode { get; init; } = "deep";
}
