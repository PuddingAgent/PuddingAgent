using PuddingCode.Models;

namespace PuddingCode.Platform;

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
    /// <summary>由 Platform 下发的 LLM 配置；Controller 优先使用，缺省回退 .env。</summary>
    public LlmConfig? LlmConfig { get; init; }
}

/// <summary>Controller 返回给 Runtime 的 LLM 代理响应。</summary>
public sealed record ControllerLlmChatResponse
{
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ReasoningContent { get; init; }
}
