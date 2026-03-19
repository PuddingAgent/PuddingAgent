using PuddingCode.Models;
using PuddingCode.Platform;

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
}
