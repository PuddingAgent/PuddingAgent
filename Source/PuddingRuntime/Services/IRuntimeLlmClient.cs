using PuddingCode.Models;

namespace PuddingRuntime.Services;

/// <summary>Runtime 侧 LLM 客户端抽象（默认经 Controller 转发）。</summary>
public interface IRuntimeLlmClient
{
    Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default);
}
