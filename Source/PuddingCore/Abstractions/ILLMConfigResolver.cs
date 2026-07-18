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
    /// [Obsolete] 解析显意识 LLM 配置。请改用 ResolveAsync(AgentLlmBinding)。
    /// </summary>
    [Obsolete("Use ResolveAsync(AgentLlmBinding) instead. Template-based resolution is deprecated.")]
    Task<LlmRoutingConfig?> ResolveConsciousAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 从 Agent 实例的 LLM binding 解析路由：根据 providerId/modelId/profileId
    /// 从 llm.providers.json 补齐 endpoint/key。不再依赖模板文件。
    /// </summary>
    Task<LlmRoutingConfig?> ResolveAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default);

    /// <summary>
    /// [Obsolete] 解析潜意识 LLM 配置。请改用 ResolveMemoryAsync(AgentLlmBinding)。
    /// </summary>
    [Obsolete("Use ResolveMemoryAsync(AgentLlmBinding) instead.")]
    Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 从 Agent 实例的 LLM binding 解析记忆 LLM 路由。
    /// </summary>
    Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default);
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
