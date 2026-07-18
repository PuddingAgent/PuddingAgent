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
    /// 从唯一配置源解析不可变 Provider/Model 身份和调用配置。
    /// modelRoute 支持 providerId/modelId 或唯一的纯 modelId；为空时解析平台默认路由。
    /// requiredCapabilityTags 仅在 modelRoute 为空时参与模型选择。
    /// </summary>
    Task<ResolvedLlmRoute> ResolveRouteAsync(
        string? modelRoute = null,
        IReadOnlyCollection<string>? requiredCapabilityTags = null,
        CancellationToken ct = default);
}

/// <summary>
/// 配置解析边界返回的不可变 Provider/Model 路由。
/// Profile 属于调用语义，由上层 invocation boundary 单独赋值。
/// </summary>
public sealed record ResolvedLlmRoute
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required LlmConfig Config { get; init; }
}
