using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>
/// LLM 调用 Profile — 将 provider/profile/model 严格分离。
/// 禁止把 ProfileId 和 ModelId 混用。
/// </summary>
public sealed record LlmInvocationProfile
{
    /// <summary>LLM 服务商标识（如 openai / deepseek / local）。</summary>
    public required string ProviderId { get; init; }
    /// <summary>Provider 内 profile 名（如 conscious.default / subconscious.default）。</summary>
    public required string ProfileId { get; init; }
    /// <summary>具体模型 ID（如 gpt-4o / deepseek-chat）。</summary>
    public required string ModelId { get; init; }
    /// <summary>LLM 角色：conscious / subconscious。</summary>
    public string Role { get; init; } = "conscious";
}

/// <summary>LLM 调用请求。</summary>
public sealed record LlmInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    /// <summary>LLM 调用 Profile（provider/profile/model/role 完整建模）。</summary>
    public required LlmInvocationProfile Profile { get; init; }
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

/// <summary>LLM Profile 解析结果 — provider/profile/model/role 完整记录。</summary>
public sealed record ResolvedLlmInvocationProfile
{
    public required string ProviderId { get; init; }
    public required string ProfileId { get; init; }
    public required string ModelId { get; init; }
    public required string Role { get; init; }
    public required LlmConfig Config { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>LLM Profile 解析器 — 将 provider/profile/model/role 解析为可调用的 LlmConfig。</summary>
public interface ILlmProfileResolver
{
    Task<ResolvedLlmInvocationProfile> ResolveAsync(
        string workspaceId,
        string agentInstanceId,
        LlmInvocationProfile profile,
        CancellationToken ct = default);
}
