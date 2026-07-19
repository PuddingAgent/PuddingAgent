using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingCode.Platform;

public sealed record LlmToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolParameterSchema Parameters { get; init; }
    /// <summary>
    /// Runtime exposure policy. MainAgentOnly is never exposed to sub-agents;
    /// DelegatedSubAgent additionally requires an allowed delegation depth and capability whitelist.
    /// </summary>
    public SubAgentExposure SubAgentExposure { get; init; } = SubAgentExposure.Default;
}

/// <summary>
/// Runtime -> Controller 的 LLM 代理请求。
/// 由 Controller 统一持有密钥并调用外部 LLM 服务商。
/// </summary>
public sealed record ControllerLlmChatRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    /// <summary>由 Platform 下发的 LLM 配置；Provider/Model 配置唯一来源是 data/config/llm.providers.json。</summary>
    public LlmConfig? LlmConfig { get; init; }
    /// <summary>可供模型 function-call 的工具定义。</summary>
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }
}

/// <summary>Controller 返回给 Runtime 的 LLM 代理响应。</summary>
public sealed record ControllerLlmChatResponse
{
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ReasoningContent { get; init; }
    public TokenUsageDto? Usage { get; init; }
}
