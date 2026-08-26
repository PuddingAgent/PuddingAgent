using System.Net.Http.Headers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PlatformLlmOptions = PuddingCode.Platform.Options.LlmOptions;

namespace PuddingCode.Core;

/// <summary>
/// 基于 OpenAI Chat Completions API 兼容协议的 LLM 网关。
/// 支持 Claude（通过 OpenAI 兼容端点）、DeepSeek、GPT 等。
/// 同时支持非流式（ChatAsync）和流式（ChatStreamAsync）调用。
/// </summary>
public sealed class OpenAiLlmGateway(HttpClient httpClient, LlmOptions options) : ILlmGateway
{
    /// <summary>Provider compatibility settings (K3, etc.). Set after construction.</summary>
    public ProviderCompatConfig? Compat { get; set; }

    /// <summary>Vision artifact resolver — injected by the runtime before each call. Set after construction.</summary>
    public IVisualArtifactResolver? VisualArtifactResolver { get; set; }
    /// <summary>图片请求预算（ADR-077）；由 DirectLlmClient 按快照策略注入，默认产品上限。</summary>
    public VisionRequestPolicy? VisionPolicy { get; set; }

    /// <summary>Audio artifact resolver — injected only for models tagged with the audio capability.</summary>
    public IAudioArtifactResolver? AudioArtifactResolver { get; set; }

    /// <summary>Workspace ID for multimodal artifact resolution.</summary>
    public string? WorkspaceId { get; set; }

    private string? _thinkingMode = NormalizeThinkingMode(options.EnableThinking switch
    {
        true => "enabled",
        false => "disabled",
        _ => null
    });

    public OpenAiLlmGateway(HttpClient httpClient, PlatformLlmOptions options)
        : this(httpClient, new LlmOptions(
            options.Endpoint,
            options.ApiKey,
            options.Model,
            options.Temperature,
            options.MaxTokens,
            options.ReasoningEffort,
            EnableThinking: null))
    {
        _thinkingMode = NormalizeThinkingMode(options.ThinkingMode);
    }

    private readonly string _chatEndpoint = NormalizeChatEndpoint(options.Endpoint);

    // ──────── Non-streaming ────────

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default)
    {
        var requestBody = await BuildRequestBody(messages, tools, stream: false, ct);
        var request = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LLM API error ({response.StatusCode}): {json}",
                inner: null,
                response.StatusCode);

        return ParseResponse(json);
    }

    // ──────── Streaming (SSE) ────────

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = await BuildRequestBody(messages, tools, stream: true, ct);
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
            throw new HttpRequestException(
                $"LLM API error ({response.StatusCode}): {errorJson}",
                inner: null,
                response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        long chunkIndex = 0;
        long? lastProviderChunkAt = null;
        var toolCallIdsByIndex = new Dictionary<int, string>();
        var toolCallIdOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        var synthesizedToolCallIndexes = new HashSet<int>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var readStartedAt = Stopwatch.GetTimestamp();
            var line = await reader.ReadLineAsync(ct);
            var readMs = ElapsedMilliseconds(readStartedAt);

            if (line is null) break;

            if (string.IsNullOrEmpty(line)) continue;           // blank separator
            if (!line.StartsWith("data: ")) continue;           // skip comments / other

            var data = line["data: ".Length..];
            if (data == "[DONE]") yield break;

            var providerChunkAt = Stopwatch.GetTimestamp();
            var providerGapMs = lastProviderChunkAt.HasValue
                ? ElapsedMilliseconds(lastProviderChunkAt.Value, providerChunkAt)
                : (long?)null;
            lastProviderChunkAt = providerChunkAt;
            chunkIndex++;

            var parseStartedAt = Stopwatch.GetTimestamp();
            var deltas = ParseStreamChunk(data);
            var parseMs = ElapsedMilliseconds(parseStartedAt);
            for (var deltaIndex = 0; deltaIndex < deltas.Count; deltaIndex++)
            {
                var delta = ResolveStreamingToolCallId(
                    deltas[deltaIndex],
                    toolCallIdsByIndex,
                    toolCallIdOwners,
                    synthesizedToolCallIndexes);
                var isFirstDeltaForChunk = deltaIndex == 0;
                yield return delta with
                {
                    ProviderChunkIndex = chunkIndex,
                    ProviderReadMs = isFirstDeltaForChunk ? readMs : null,
                    ProviderChunkGapMs = isFirstDeltaForChunk ? providerGapMs : null,
                    ProviderPayloadChars = isFirstDeltaForChunk ? data.Length : null,
                    GatewayParseMs = isFirstDeltaForChunk ? parseMs : null,
                };
            }
        }
    }

    private static long ElapsedMilliseconds(long startedAt)
        => ElapsedMilliseconds(startedAt, Stopwatch.GetTimestamp());

    private static long ElapsedMilliseconds(long startedAt, long endedAt)
        => (long)((endedAt - startedAt) * 1000.0 / Stopwatch.Frequency);

    private static IReadOnlyList<StreamDelta> ParseStreamChunk(string json)
    {
        var root = JsonNode.Parse(json);
        var usage = ParseUsage(root?["usage"]);
        var choices = root?["choices"]?.AsArray();
        if (choices is null || choices.Count == 0)
            return usage is not null ? [new StreamDelta { Usage = usage }] : [];

        var choice = choices[0];
        var delta = choice?["delta"];
        if (delta is null)
            return usage is not null ? [new StreamDelta { Usage = usage }] : [];

        var finishReason = choice?["finish_reason"]?.GetValue<string>();

        // Content delta
        var contentDelta = delta["content"]?.GetValue<string>();

        // Reasoning delta (DeepSeek Reasoner)
        var reasoningDelta = delta["reasoning_content"]?.GetValue<string>();

        if (delta["tool_calls"] is not JsonArray tcArray || tcArray.Count == 0)
        {
            if (contentDelta is null && reasoningDelta is null
                && finishReason is null && usage is null)
                return [];

            return
            [
                new StreamDelta
                {
                    ContentDelta = contentDelta,
                    ReasoningDelta = reasoningDelta,
                    FinishReason = finishReason,
                    Usage = usage,
                },
            ];
        }

        // OpenAI-compatible providers may emit several tool calls in one SSE
        // chunk. Preserve every entry and attach the shared content/usage fields
        // only to the first emitted delta so downstream metrics are not doubled.
        var result = new List<StreamDelta>(tcArray.Count);
        for (var i = 0; i < tcArray.Count; i++)
        {
            var tc = tcArray[i];
            var func = tc?["function"];
            result.Add(new StreamDelta
            {
                ContentDelta = i == 0 ? contentDelta : null,
                ReasoningDelta = i == 0 ? reasoningDelta : null,
                ToolCallIndex = tc?["index"]?.GetValue<int>(),
                ToolCallId = tc?["id"]?.GetValue<string>(),
                ToolCallNameDelta = func?["name"]?.GetValue<string>(),
                ToolCallArgsDelta = func?["arguments"]?.GetValue<string>(),
                FinishReason = i == 0 ? finishReason : null,
                Usage = i == 0 ? usage : null,
            });
        }

        return result;
    }

    private static StreamDelta ResolveStreamingToolCallId(
        StreamDelta delta,
        Dictionary<int, string> idsByIndex,
        Dictionary<string, int> idOwners,
        HashSet<int> synthesizedIndexes)
    {
        if (delta.ToolCallIndex is not int callIndex)
            return delta;

        var providerId = string.IsNullOrWhiteSpace(delta.ToolCallId)
            ? null
            : delta.ToolCallId;
        if (providerId is not null
            && (!idOwners.TryGetValue(providerId, out var ownerIndex) || ownerIndex == callIndex))
        {
            idsByIndex[callIndex] = providerId;
            idOwners[providerId] = callIndex;
            synthesizedIndexes.Remove(callIndex);
            return delta with
            {
                ToolCallId = providerId,
                ToolCallIdWasSynthesized = false,
            };
        }

        if (!idsByIndex.TryGetValue(callIndex, out var resolvedId))
        {
            do
            {
                resolvedId = CreateSyntheticToolCallId();
            }
            while (idOwners.ContainsKey(resolvedId));

            idsByIndex[callIndex] = resolvedId;
            idOwners[resolvedId] = callIndex;
            synthesizedIndexes.Add(callIndex);
        }

        return delta with
        {
            ToolCallId = resolvedId,
            ToolCallIdWasSynthesized = synthesizedIndexes.Contains(callIndex),
        };
    }

    private static string CreateSyntheticToolCallId()
        => $"call_pudding_{Guid.NewGuid():N}"[..40];

    // ──────── Helpers ────────

    private static string NormalizeChatEndpoint(string endpoint)
    {
        var url = endpoint.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;
        return url + "/chat/completions";
    }

    private async Task<string> BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        bool stream,
        CancellationToken ct = default)
    {
        var messagesArray = new JsonArray();
        var protocolSafeMessages = LlmMessageSequenceNormalizer.Normalize(messages).Messages;
        // K3 compat: read compat config once before message loop
        var compat = Compat;

        foreach (var msg in protocolSafeMessages)
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

            // ADR-077：Chat Completions 协议不支持图片型工具结果；不得伪装成 user message 绕过协议。
            if (msg.Role == ChatRole.Tool
                && ChatMessageMultimodalNormalizer.GetImageParts(msg).Count > 0)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.ToolOutputNotSupported,
                    "This route uses the Chat Completions protocol, which cannot carry image tool results; " +
                    "use mode=delegate or a responses-protocol route.");
            }

            if (msg.Content is not null)
            {
                // ── Provider-authorized multimodal content ──
                var imageParts = ChatMessageMultimodalNormalizer.GetImageParts(msg);
                if (msg.Role == ChatRole.User
                    && imageParts.Count > 0
                    && VisualArtifactResolver is not null
                    && !string.IsNullOrWhiteSpace(WorkspaceId))
                {
                    // ADR-077 §5.5：无视觉解析通道 = 文本路由，图片部件不进入请求；
                    // 有解析通道而失败由 Planner fail closed。
                    msgObj["content"] = await BuildMultimodalContentArrayAsync(msg, imageParts, ct)
                        ?? (JsonNode?)msg.Content;
                }
                else if (msg.Role == ChatRole.User
                         && msg.AudioArtifactIds is { Count: > 0 }
                         && AudioArtifactResolver is not null
                         && !string.IsNullOrWhiteSpace(WorkspaceId))
                {
                    msgObj["content"] = await BuildMultimodalContentArrayAsync(msg, [], ct)
                        ?? (JsonNode?)msg.Content;
                }
                else
                {
                    msgObj["content"] = msg.Content;
                }
            }
            else
                msgObj["content"] = (JsonNode?)null;

            // DeepSeek Reasoner / K3 compat: include reasoning_content in messages
            if (msg.ReasoningContent is not null)
                msgObj["reasoning_content"] = msg.ReasoningContent;
            else if (compat?.RequiresReasoningContentInToolMessages == true && msg.Role == ChatRole.Assistant && msg.ToolCalls is { Count: > 0 })
                msgObj["reasoning_content"] = ""; // K3 requires reasoning_content when tool_calls present

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

        if (stream)
        {
            // OpenAI-compatible providers only emit final usage in streaming mode
            // when explicitly requested via stream_options.include_usage.
            // K3 compat: skip when provider does not support usage in streaming
            if (compat?.SupportsUsageInStreaming != false)
            {
                requestObj["stream_options"] = new JsonObject
                {
                    ["include_usage"] = true
                };
            }
        }

        string maxTokensKey = compat?.MaxTokensField ?? "max_tokens";

        if (options.Temperature.HasValue)
            requestObj["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            requestObj[maxTokensKey] = options.MaxTokens.Value;
        if (options.ReasoningEffort is not null)
            requestObj["reasoning_effort"] = options.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(_thinkingMode))
        {
            if (compat?.UseReasoningEffort != true)
            {
                requestObj["thinking"] = new JsonObject
                {
                    ["type"] = _thinkingMode
                };
            }
            else if (compat?.DefaultReasoningEffort is not null && options.ReasoningEffort is null)
            {
                requestObj["reasoning_effort"] = compat.DefaultReasoningEffort;
            }
        }

        if (tools.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in tools)
            {
                JsonNode parametersNode;
                if (tool.Parameters.RawJsonSchema is { ValueKind: JsonValueKind.Object } rawSchema)
                {
                    parametersNode = JsonNode.Parse(rawSchema.GetRawText())
                        ?? throw new InvalidOperationException($"Tool '{tool.Name}' has an invalid raw JSON schema.");
                }
                else
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

                    parametersNode = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = propsObj,
                        ["required"] = requiredArray
                    };
                }

                toolsArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = parametersNode
                    }
                });
            }
            requestObj["tools"] = toolsArray;
        }

        return requestObj.ToJsonString();
    }

    /// <summary>
    /// Build an OpenAI-compatible multimodal content array for a user message.
    /// Vision parts resolve through the fail-closed planner (ADR-077); audio keeps its
    /// per-artifact tolerant path.
    /// </summary>
    private async Task<JsonArray?> BuildMultimodalContentArrayAsync(
        ChatMessage msg,
        IReadOnlyList<LlmImagePart> imageParts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
            return null;

        var content = new JsonArray();
        var resolvedAny = false;

        if (imageParts.Count > 0 && VisualArtifactResolver is not null)
        {
                        var plan = await LlmVisualInputPlanner.PlanAsync(
                WorkspaceId!,
                imageParts,
                VisualArtifactResolver,
                policy: VisionPolicy,
                ct: ct);
                        foreach (var image in plan.Images)
            {
                var dataUri = image.DataUri
                    ?? throw new VisionPipelineException(
                        VisionErrorCodes.MediaInvalid,
                        $"Image artifact {image.ArtifactId} resolved to a provider file reference; " +
                        "this protocol does not support file_id image inputs.");
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = dataUri,
                        ["detail"] = string.Equals(image.Detail, VisionContentPartDetails.Low, StringComparison.Ordinal)
                            ? "low"
                            : "high",
                    },
                });
                resolvedAny = true;
            }
        }

        if (msg.AudioArtifactIds is { Count: > 0 }
            && AudioArtifactResolver is not null)
        {
            foreach (var artifactId in msg.AudioArtifactIds)
            {
                try
                {
                    var resolved = await AudioArtifactResolver.ResolveAsync(
                        WorkspaceId!,
                        artifactId,
                        ct);
                    if (resolved is not null)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = new JsonObject
                            {
                                ["data"] = resolved.DataUri,
                                ["format"] = resolved.Format,
                            },
                        });
                        resolvedAny = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[OpenAiLlmGateway] Failed to resolve audio artifact '{artifactId}': {ex.Message}");
                }
            }
        }

        if (!resolvedAny)
            return null;

        // Text precedes binary modalities so the model sees the user intent first.
        if (!string.IsNullOrWhiteSpace(msg.Content))
        {
            content.Insert(0, new JsonObject
            {
                ["type"] = "text",
                ["text"] = msg.Content,
            });
        }

        return content;
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
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tc in tcArray)
            {
                var id = tc?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                {
                    do
                    {
                        id = CreateSyntheticToolCallId();
                    }
                    while (!seenIds.Add(id));
                }
                var func = tc!["function"]!;
                var name = func["name"]!.GetValue<string>();
                var arguments = func["arguments"]!.GetValue<string>();
                toolCalls.Add(new ToolCall(id, name, arguments));
            }
        }

        return new LlmResponse(content, toolCalls, reasoningContent, ParseUsage(root?["usage"]));
    }

    private static TokenUsageDto? ParseUsage(JsonNode? usage)
    {
        if (usage is null) return null;

        // DeepSeek 格式：prompt_cache_hit_tokens / prompt_cache_miss_tokens 直接在 usage 下
        var cacheHit = ReadInt(usage, "prompt_cache_hit_tokens");
        var cacheMiss = ReadInt(usage, "prompt_cache_miss_tokens");

        // OpenAI 格式：prompt_tokens_details.cached_tokens 作为 fallback
        if (cacheHit is null && cacheMiss is null)
        {
            var details = usage["prompt_tokens_details"];
            if (details is not null)
            {
                var cached = ReadInt(details, "cached_tokens");
                if (cached.HasValue)
                {
                    var totalPrompt = ReadInt(usage, "prompt_tokens") ?? 0;
                    cacheHit = cached.Value;
                    cacheMiss = totalPrompt - cached.Value;
                }
            }
        }

        return new TokenUsageDto
        {
            PromptTokens = ReadInt(usage, "prompt_tokens"),
            CompletionTokens = ReadInt(usage, "completion_tokens"),
            TotalTokens = ReadInt(usage, "total_tokens"),
            PromptCacheHitTokens = cacheHit,
            PromptCacheMissTokens = cacheMiss,
        };
    }

    private static int? ReadInt(JsonNode usage, string propertyName)
    {
        try
        {
            return usage[propertyName]?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeThinkingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "auto" or "enabled" or "disabled" ? normalized : null;
    }
}
