using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

        return new LlmResponse(payload.Content, payload.ToolCalls, payload.ReasoningContent, payload.Usage);
    }

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "[LlmBridge] STREAM ws={Ws} session={Session} template={Template} msgCount={Count} hasLlmConfig={HasConfig}",
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/internal/llm/chat/stream")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "[LlmBridge] STREAM ERROR {Status} ws={Ws} session={Session} body={Body}",
                (int)response.StatusCode, workspaceId, sessionId, errorBody);
            throw new HttpRequestException(
                $"Controller LLM stream returned {(int)response.StatusCode}: {errorBody}");
        }

        await foreach (var frame in ReadSseFramesAsync(response, ct))
        {
            if (frame.Event == "delta")
            {
                var delta = JsonSerializer.Deserialize<StreamDelta>(frame.Data, JsonOptions);
                if (delta is not null)
                    yield return delta;
            }
            else if (frame.Event == "usage")
            {
                var usage = JsonSerializer.Deserialize<TokenUsageDto>(frame.Data, JsonOptions);
                if (usage is not null)
                    yield return new StreamDelta { Usage = usage };
            }
            else if (frame.Event == "error")
            {
                throw new HttpRequestException($"Controller LLM stream error: {frame.Data}");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async IAsyncEnumerable<ServerSentEventFrame> ReadSseFramesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);

            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (eventName is not null && data.Length > 0)
                {
                    yield return new ServerSentEventFrame(eventName, data.ToString());
                }
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventName = line["event: ".Length..].Trim();
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.Append(line["data: ".Length..]);
        }
    }
}
