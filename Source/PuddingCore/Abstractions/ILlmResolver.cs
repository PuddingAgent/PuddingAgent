using PuddingCode.Models;

using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// LLM 服务商与模型解析服务 — 根据 providerId + modelId 从统一配置源
/// 获取完整的 LlmConfig（Endpoint、ApiKey、ModelId、MaxContextTokens）。
/// 
/// 职责分离：调用方不直接访问 IConfiguration、文件或数据库，
/// 由实现类适配 data/config/llm.providers.json。
/// </summary>
public interface ILlmResolver
{
    /// <summary>
    /// 根据服务商 ID 和模型 ID 解析完整的 LLM 配置。
    /// modelId 可选 — 不指定时使用 provider 的默认模型。
    /// 返回 null 表示 provider 不存在或未启用。
    /// </summary>
    Task<LlmConfig?> ResolveAsync(string providerId, string? modelId = null, CancellationToken ct = default);

    /// <summary>
    /// 获取默认的 LLM 配置（平台首选 provider + 默认模型）。
    /// 全局默认必须存在，否则抛出异常。
    /// </summary>
    Task<LlmConfig> ResolveDefaultAsync(CancellationToken ct = default);

    Task<LlmConfig?> ResolveByCapabilityAsync(
        string[] requiredTags,
        string[]? preferredTags = null,
        string? providerId = null,
        CancellationToken ct = default);

    IReadOnlyList<LlmModelInfo> GetAllModels();

    /// <summary>
    /// 列出所有已启用的 provider ID（供 Agent 选择模型时参考）。
    /// </summary>
    Task<IReadOnlyList<string>> ListEnabledProviderIdsAsync(CancellationToken ct = default);
}
