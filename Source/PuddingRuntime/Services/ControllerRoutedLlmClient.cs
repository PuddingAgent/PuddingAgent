using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 通过 Controller 调用 LLM 的客户端实现。
/// Runtime 不持有外部 LLM 密钥。
/// </summary>
public sealed class ControllerRoutedLlmClient(
    HttpClient httpClient,
    ILogger<ControllerRoutedLlmClient> logger) : IRuntimeLlmClient
{
    public async Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "[LlmBridge] REQUEST ws={Ws} session={Session} template={Template} msgCount={Count} hasLlmConfig={HasConfig}",
            workspaceId, sessionId, agentTemplateId, messages.Count, llmConfig is not null);

        var request = new ControllerLlmChatRequest
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentTemplateId = agentTemplateId,
            Messages = messages,
            Tools = tools,
            LlmConfig = llmConfig,
        };

        var response = await httpClient.PostAsJsonAsync("/api/internal/llm/chat", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            logger.LogError(
                "[LlmBridge] ERROR {Status} ws={Ws} session={Session} elapsed={Elapsed}ms body={Body}",
                (int)response.StatusCode, workspaceId, sessionId, sw.ElapsedMilliseconds, errorBody);
            throw new HttpRequestException(
                $"Controller LLM proxy returned {(int)response.StatusCode}: {errorBody}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ControllerLlmChatResponse>(ct);
        if (payload is null)
            throw new InvalidOperationException("Empty response from Controller LLM proxy.");

        sw.Stop();
        logger.LogInformation(
            "[LlmBridge] OK ws={Ws} session={Session} contentLen={Len} elapsed={Elapsed}ms",
            workspaceId, sessionId, payload.Content?.Length ?? 0, sw.ElapsedMilliseconds);

        return new LlmResponse(payload.Content, payload.ToolCalls, payload.ReasoningContent);
    }
}
