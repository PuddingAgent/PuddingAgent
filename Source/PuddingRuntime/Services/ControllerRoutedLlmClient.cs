using System.Net.Http.Json;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 通过 Controller 调用 LLM 的客户端实现。
/// Runtime 不持有外部 LLM 密钥。
/// </summary>
public sealed class ControllerRoutedLlmClient(HttpClient httpClient) : IRuntimeLlmClient
{
    public async Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        var request = new ControllerLlmChatRequest
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentTemplateId = agentTemplateId,
            Messages = messages,
        };

        var response = await httpClient.PostAsJsonAsync("/api/internal/llm/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ControllerLlmChatResponse>(ct);
        if (payload is null)
            throw new InvalidOperationException("Empty response from Controller LLM proxy.");

        return new LlmResponse(payload.Content, payload.ToolCalls, payload.ReasoningContent);
    }
}
