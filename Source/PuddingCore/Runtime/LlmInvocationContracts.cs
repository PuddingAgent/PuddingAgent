using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>LLM 调用请求。</summary>
public sealed record LlmInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string ProfileId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = Array.Empty<LlmToolDefinition>();
    public RuntimeTraceContext? Trace { get; init; }
}

/// <summary>LLM 调用结果。</summary>
public sealed record LlmInvocationResult
{
    public required bool Success { get; init; }
    public string? ReplyText { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();
    public TokenUsageDto? Usage { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Error { get; init; }
}

/// <summary>LLM 调用服务，隔离执行引擎与 provider 协议细节。</summary>
public interface ILlmInvocationService
{
    Task<LlmInvocationResult> InvokeAsync(LlmInvocationRequest request, CancellationToken ct = default);
    IAsyncEnumerable<StreamDelta> InvokeStreamAsync(LlmInvocationRequest request, CancellationToken ct = default);
}
