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
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        var request = new ControllerLlmChatRequest
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentTemplateId = agentTemplateId,
            Messages = messages,
            LlmConfig = llmConfig,
        };

        var response = await httpClient.PostAsJsonAsync("/api/internal/llm/chat", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Controller LLM proxy returned {(int)response.StatusCode}: {errorBody}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ControllerLlmChatResponse>(ct);
        if (payload is null)
            throw new InvalidOperationException("Empty response from Controller LLM proxy.");

        return new LlmResponse(payload.Content, payload.ToolCalls, payload.ReasoningContent);
    }
}
