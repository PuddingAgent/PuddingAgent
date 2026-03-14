using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>
/// 基于 OpenAI Chat Completions API 兼容协议的 LLM 网关。
/// 支持 Claude（通过 OpenAI 兼容端点）、DeepSeek、GPT 等。
/// 同时支持非流式（ChatAsync）和流式（ChatStreamAsync）调用。
/// </summary>
public sealed class OpenAiLlmGateway(HttpClient httpClient, LlmOptions options) : ILlmGateway
{
    private readonly string _chatEndpoint = NormalizeChatEndpoint(options.Endpoint);

    // ──────── Non-streaming ────────

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, tools, stream: false);
        var request = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM API error ({response.StatusCode}): {json}");

        return ParseResponse(json);
    }

    // ──────── Streaming (SSE) ────────

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, tools, stream: true);
        var request = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LLM API error ({response.StatusCode}): {errorJson}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);

            if (string.IsNullOrEmpty(line)) continue;           // blank separator
            if (!line.StartsWith("data: ")) continue;           // skip comments / other

            var data = line["data: ".Length..];
            if (data == "[DONE]") yield break;

            var delta = ParseStreamChunk(data);
            if (delta is not null)
                yield return delta;
        }
    }

    private static StreamDelta? ParseStreamChunk(string json)
    {
        var root = JsonNode.Parse(json);
        var choices = root?["choices"]?.AsArray();
        if (choices is null || choices.Count == 0) return null;

        var choice = choices[0];
        var delta = choice?["delta"];
        if (delta is null) return null;

        var finishReason = choice?["finish_reason"]?.GetValue<string>();

        // Content delta
        var contentDelta = delta["content"]?.GetValue<string>();

        // Reasoning delta (DeepSeek Reasoner)
        var reasoningDelta = delta["reasoning_content"]?.GetValue<string>();

        // Tool call deltas
        string? tcId = null, tcNameDelta = null, tcArgsDelta = null;
        int? tcIndex = null;

        if (delta["tool_calls"] is JsonArray tcArray && tcArray.Count > 0)
        {
            var tc = tcArray[0];
            tcIndex = tc?["index"]?.GetValue<int>();
            tcId = tc?["id"]?.GetValue<string>();
            var func = tc?["function"];
            tcNameDelta = func?["name"]?.GetValue<string>();
            tcArgsDelta = func?["arguments"]?.GetValue<string>();
        }

        // Skip empty deltas (only happens on very first chunk sometimes)
        if (contentDelta is null && reasoningDelta is null
            && tcIndex is null && finishReason is null)
            return null;

        return new StreamDelta
        {
            ContentDelta = contentDelta,
            ReasoningDelta = reasoningDelta,
            ToolCallIndex = tcIndex,
            ToolCallId = tcId,
            ToolCallNameDelta = tcNameDelta,
            ToolCallArgsDelta = tcArgsDelta,
            FinishReason = finishReason
        };
    }

    // ──────── Helpers ────────

    private static string NormalizeChatEndpoint(string endpoint)
    {
        var url = endpoint.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;
        return url + "/chat/completions";
    }

    private string BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        bool stream)
    {
        var messagesArray = new JsonArray();

        foreach (var msg in messages)
        {
            var msgObj = new JsonObject
            {
                ["role"] = msg.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    ChatRole.Tool => "tool",
                    _ => "user"
                }
            };

            if (msg.Content is not null)
                msgObj["content"] = msg.Content;
            else
                msgObj["content"] = (JsonNode?)null;

            // DeepSeek Reasoner: include reasoning_content in assistant messages
            if (msg.ReasoningContent is not null)
                msgObj["reasoning_content"] = msg.ReasoningContent;

            if (msg.ToolCallId is not null)
                msgObj["tool_call_id"] = msg.ToolCallId;

            if (msg.ToolCalls is { Count: > 0 })
            {
                var toolCallsArray = new JsonArray();
                foreach (var tc in msg.ToolCalls)
                {
                    toolCallsArray.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.ArgumentsJson
                        }
                    });
                }
                msgObj["tool_calls"] = toolCallsArray;
            }

            messagesArray.Add(msgObj);
        }

        var requestObj = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = messagesArray,
            ["stream"] = stream
        };

        if (options.Temperature.HasValue)
            requestObj["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            requestObj["max_tokens"] = options.MaxTokens.Value;

        if (tools.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in tools)
            {
                var propsObj = new JsonObject();
                foreach (var p in tool.Parameters.Properties)
                {
                    propsObj[p.Name] = new JsonObject
                    {
                        ["type"] = p.Type,
                        ["description"] = p.Description
                    };
                }

                var requiredArray = new JsonArray();
                foreach (var r in tool.Parameters.Required)
                    requiredArray.Add(JsonValue.Create(r));

                toolsArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = propsObj,
                            ["required"] = requiredArray
                        }
                    }
                });
            }
            requestObj["tools"] = toolsArray;
        }

        return requestObj.ToJsonString();
    }

    private static LlmResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json);
        var choices = root?["choices"]?.AsArray();
        if (choices is null || choices.Count == 0)
            throw new InvalidOperationException($"Invalid LLM response: no choices. Response: {json}");

        var message = choices[0]?["message"];
        if (message is null)
            throw new InvalidOperationException($"Invalid LLM response: no message. Response: {json}");

        var content = message["content"]?.GetValue<string>();

        // DeepSeek Reasoner returns reasoning_content alongside content
        var reasoningContent = message["reasoning_content"]?.GetValue<string>();

        List<ToolCall>? toolCalls = null;
        if (message["tool_calls"] is JsonArray tcArray && tcArray.Count > 0)
        {
            toolCalls = [];
            foreach (var tc in tcArray)
            {
                var id = tc!["id"]!.GetValue<string>();
                var func = tc["function"]!;
                var name = func["name"]!.GetValue<string>();
                var arguments = func["arguments"]!.GetValue<string>();
                toolCalls.Add(new ToolCall(id, name, arguments));
            }
        }

        return new LlmResponse(content, toolCalls, reasoningContent);
    }
}
