using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// LLM 配置解析器：从 Agent 模板 + 环境变量中解析显意识/潜意识 LLM 路由。
/// 与 IAgentTemplateProvider 分离：后者负责个性/提示词，本接口负责 LLM 基础设施配置。
/// 不受模板 IsEnabled 影响——禁用模板的 LLM 配置仍然可读。
/// </summary>
public interface ILLMConfigResolver
{
    /// <summary>
    /// 解析显意识 LLM 配置：ProviderId + ModelId + Endpoint + ApiKey。
    /// 优先级：WorkspaceAgentTemplate > GlobalAgentTemplate > 环境变量。
    /// </summary>
    Task<LlmRoutingConfig?> ResolveConsciousAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 解析潜意识 LLM 配置：MemoryLlmProviderId/ModelId + MemorySearchMode。
    /// Endpoint 与 ApiKey 从 LLM 资源池解析；旧 Endpoint/ApiKey 字段只作为兼容回退。
    /// </summary>
    Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default);
}

/// <summary>显意识 LLM 路由配置。</summary>
public sealed record LlmRoutingConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
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
