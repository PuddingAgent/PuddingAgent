using PuddingCode.Models;
using PuddingCode.Platform;
using System.Runtime.CompilerServices;

namespace PuddingRuntime.Services;

/// <summary>Runtime 侧 LLM 客户端抄象（默认经 Controller 转发）。</summary>
public interface IRuntimeLlmClient
{
    Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default);

    /// <summary>流式调用 LLM，逐个返回 provider delta/usage。</summary>
    IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default);
}
