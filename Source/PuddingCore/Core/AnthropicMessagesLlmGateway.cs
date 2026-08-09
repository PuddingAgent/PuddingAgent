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
/// Anthropic Messages API gateway with buffered and streaming text, thinking,
/// multimodal input, and client tool-use support.
/// </summary>
public sealed class AnthropicMessagesLlmGateway(HttpClient httpClient, LlmOptions options) : ILlmGateway
{
    private const string ProtocolName = "anthropic";
    private const string AnthropicVersion = "2023-06-01";

    public IVisualArtifactResolver? VisualArtifactResolver { get; set; }
    public string? WorkspaceId { get; set; }

    public AnthropicMessagesLlmGateway(HttpClient httpClient, PlatformLlmOptions options)
        : this(httpClient, new LlmOptions(
            options.Endpoint,
            options.ApiKey,
            options.Model,
            options.Temperature,
            options.MaxTokens,
            options.ReasoningEffort,
            EnableThinking: null))
    {
    }

    private readonly string _messagesEndpoint = NormalizeMessagesEndpoint(options.Endpoint);

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default)
    {
        var requestBody = await BuildRequestBodyAsync(messages, tools, stream: false, ct);
        using var request = CreateRequest(requestBody);
        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"LLM Anthropic Messages API error ({response.StatusCode}): {json}",
                inner: null,
                response.StatusCode);
        }

        return ParseResponse(json);
    }

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = await BuildRequestBodyAsync(messages, tools, stream: true, ct);
        using var request = CreateRequest(requestBody);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LLM Anthropic Messages API error ({response.StatusCode}): {errorJson}",
                inner: null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new AnthropicStreamParser();
        long chunkIndex = 0;
        long? lastProviderChunkAt = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var readStartedAt = Stopwatch.GetTimestamp();
            var line = await reader.ReadLineAsync(ct);
            var readMs = ElapsedMilliseconds(readStartedAt);

            if (line is null)
                yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0 || data == "[DONE]")
                continue;

            var providerChunkAt = Stopwatch.GetTimestamp();
            var providerGapMs = lastProviderChunkAt.HasValue
                ? ElapsedMilliseconds(lastProviderChunkAt.Value, providerChunkAt)
                : (long?)null;
            lastProviderChunkAt = providerChunkAt;
            chunkIndex++;

            var parseStartedAt = Stopwatch.GetTimestamp();
            var deltas = parser.Parse(data);
            var parseMs = ElapsedMilliseconds(parseStartedAt);
            for (var deltaIndex = 0; deltaIndex < deltas.Count; deltaIndex++)
            {
                var delta = deltas[deltaIndex];
                var firstForChunk = deltaIndex == 0;
                yield return delta with
                {
                    ProviderChunkIndex = chunkIndex,
                    ProviderReadMs = firstForChunk ? readMs : null,
                    ProviderChunkGapMs = firstForChunk ? providerGapMs : null,
                    ProviderPayloadChars = firstForChunk ? data.Length : null,
                    GatewayParseMs = firstForChunk ? parseMs : null,
                };
            }
        }
    }

    private HttpRequestMessage CreateRequest(string requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        return request;
    }

    private async Task<string> BuildRequestBodyAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        bool stream,
        CancellationToken ct)
    {
        var normalized = LlmMessageSequenceNormalizer.Normalize(messages).Messages;
        var root = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens ?? 4096,
            ["stream"] = stream,
        };

        var system = string.Join(
            "\n\n",
            normalized
                .Where(message => message.Role == ChatRole.System)
                .Select(message => message.Content)
                .Where(content => !string.IsNullOrWhiteSpace(content)));
        if (!string.IsNullOrWhiteSpace(system))
            root["system"] = system;

        var messageNodes = new JsonArray();
        foreach (var message in normalized)
        {
            if (message.Role == ChatRole.System)
                continue;

            switch (message.Role)
            {
                case ChatRole.User:
                    messageNodes.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = await BuildUserContentAsync(message, ct),
                    });
                    break;
                case ChatRole.Assistant:
                    messageNodes.Add(BuildAssistantMessage(message));
                    break;
                case ChatRole.Tool:
                    AddToolResultMessage(messageNodes, message);
                    break;
            }
        }
        root["messages"] = messageNodes;

        if (tools.Count > 0)
        {
            var toolNodes = new JsonArray();
            foreach (var tool in tools)
            {
                toolNodes.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = BuildParametersNode(tool),
                });
            }
            root["tools"] = toolNodes;
        }

        if (options.Temperature.HasValue)
            root["temperature"] = options.Temperature.Value;

        return root.ToJsonString();
    }

    private async Task<JsonNode> BuildUserContentAsync(ChatMessage message, CancellationToken ct)
    {
        if (message.VisualArtifactIds is not { Count: > 0 }
            || VisualArtifactResolver is null
            || string.IsNullOrWhiteSpace(WorkspaceId))
        {
            return JsonValue.Create(message.Content ?? string.Empty)!;
        }

        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = message.Content,
            });
        }

        foreach (var artifactId in message.VisualArtifactIds)
        {
            try
            {
                var resolved = await VisualArtifactResolver.ResolveAsync(WorkspaceId!, artifactId, ct);
                if (resolved is null || !TryParseDataUri(resolved.DataUri, out var mediaType, out var data))
                    continue;

                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = mediaType,
                        ["data"] = data,
                    },
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Debug.WriteLine(
                    $"[AnthropicMessagesLlmGateway] Failed to resolve vision artifact '{artifactId}': {exception.Message}");
            }
        }

        return content.Count > (string.IsNullOrWhiteSpace(message.Content) ? 0 : 1)
            ? content
            : JsonValue.Create(message.Content ?? string.Empty)!;
    }

    private static JsonObject BuildAssistantMessage(ChatMessage message)
    {
        var content = new JsonArray();
        var replayedContinuation = false;
        if (message.ContinuationState is { OutputItemsJson.Count: > 0 } continuation
            && string.Equals(continuation.Protocol, ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var blockJson in continuation.OutputItemsJson)
            {
                content.Add(JsonNode.Parse(blockJson)
                    ?? throw new InvalidOperationException("Anthropic continuation block is invalid JSON."));
            }
            replayedContinuation = true;
        }

        if (!replayedContinuation)
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message.Content,
                });
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.Id))
                        throw new InvalidOperationException("Anthropic tool_use requires a non-empty id.");

                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ParseToolInput(call.ArgumentsJson),
                    });
                }
            }
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content.Count > 0 ? content : JsonValue.Create(string.Empty),
        };
    }

    private static void AddToolResultMessage(JsonArray messages, ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ToolCallId))
            throw new InvalidOperationException("Anthropic tool_result requires a non-empty tool_use_id.");

        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = message.ToolCallId,
            ["content"] = message.Content ?? string.Empty,
        };

        if (messages.LastOrDefault() is JsonObject previous
            && ReadString(previous, "role") == "user"
            && previous["content"] is JsonArray previousContent
            && previousContent.All(node => node is JsonObject block && ReadString(block, "type") == "tool_result"))
        {
            previousContent.Add(result);
            return;
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(result),
        });
    }

    private static JsonNode ParseToolInput(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();

        var parsed = JsonNode.Parse(argumentsJson);
        return parsed is JsonObject
            ? parsed
            : throw new InvalidOperationException("Anthropic tool_use input must be a JSON object.");
    }

    private static JsonNode BuildParametersNode(ITool tool)
    {
        if (tool.Parameters.RawJsonSchema is { ValueKind: JsonValueKind.Object } rawSchema)
        {
            return JsonNode.Parse(rawSchema.GetRawText())
                ?? throw new InvalidOperationException($"Tool '{tool.Name}' has an invalid raw JSON schema.");
        }

        var properties = new JsonObject();
        foreach (var parameter in tool.Parameters.Properties)
        {
            properties[parameter.Name] = new JsonObject
            {
                ["type"] = parameter.Type,
                ["description"] = parameter.Description,
            };
        }

        var required = new JsonArray();
        foreach (var requiredName in tool.Parameters.Required)
            required.Add(requiredName);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    private static LlmResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Invalid Anthropic Messages response: body is not a JSON object.");
        if (ReadString(root, "type") == "error")
            ThrowStreamError(root);

        var blocks = root["content"] as JsonArray
            ?? throw new InvalidOperationException("Invalid Anthropic Messages response: content is missing.");
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var continuationBlocks = new List<string>(blocks.Count);
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceBlock in blocks.OfType<JsonObject>())
        {
            var block = (JsonObject)sourceBlock.DeepClone();
            switch (ReadString(block, "type"))
            {
                case "text":
                    content.Append(ReadString(block, "text"));
                    break;
                case "thinking":
                    reasoning.Append(ReadString(block, "thinking"));
                    break;
                case "tool_use":
                {
                    var callId = EnsureUniqueCallId(ReadString(block, "id"), seenCallIds);
                    block["id"] = callId;
                    toolCalls.Add(new ToolCall(
                        callId,
                        ReadString(block, "name") ?? string.Empty,
                        block["input"]?.ToJsonString() ?? "{}"));
                    break;
                }
            }
            continuationBlocks.Add(block.ToJsonString());
        }

        return new LlmResponse(
            content.ToString(),
            toolCalls.Count > 0 ? toolCalls : null,
            reasoning.Length > 0 ? reasoning.ToString() : null,
            ParseUsage(root["usage"] as JsonObject),
            new LlmContinuationState(ProtocolName, continuationBlocks));
    }

    private static TokenUsageDto? ParseUsage(JsonObject? usage)
    {
        if (usage is null)
            return null;

        var freshInput = ReadInt(usage, "input_tokens") ?? 0;
        var cacheRead = ReadInt(usage, "cache_read_input_tokens") ?? 0;
        var cacheCreation = ReadInt(usage, "cache_creation_input_tokens") ?? 0;
        var output = ReadInt(usage, "output_tokens") ?? 0;
        var prompt = freshInput + cacheRead + cacheCreation;
        return new TokenUsageDto
        {
            PromptTokens = prompt,
            CompletionTokens = output,
            TotalTokens = prompt + output,
            PromptCacheHitTokens = cacheRead,
            PromptCacheMissTokens = freshInput + cacheCreation,
        };
    }

    private static string MapStopReason(string? stopReason)
        => stopReason switch
        {
            "tool_use" => "tool_calls",
            "max_tokens" => "length",
            "end_turn" or "stop_sequence" or "pause_turn" or "refusal" => "stop",
            null or "" => "stop",
            _ => stopReason,
        };

    private static string EnsureUniqueCallId(string? providerCallId, HashSet<string> seenCallIds)
    {
        var callId = providerCallId;
        if (!string.IsNullOrWhiteSpace(callId) && seenCallIds.Add(callId))
            return callId;

        do
        {
            callId = $"call_pudding_{Guid.NewGuid():N}"[..40];
        }
        while (!seenCallIds.Add(callId));
        return callId;
    }

    private static void ThrowStreamError(JsonObject root)
    {
        var error = root["error"] as JsonObject;
        throw new HttpRequestException(
            $"Anthropic Messages API error: {ReadString(error, "message") ?? "unknown provider error"}");
    }

    private static bool TryParseDataUri(string value, out string mediaType, out string data)
    {
        mediaType = string.Empty;
        data = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = value.IndexOf(',');
        if (separator < 0)
            return false;

        var metadata = value["data:".Length..separator];
        var semicolon = metadata.IndexOf(';');
        mediaType = semicolon >= 0 ? metadata[..semicolon] : metadata;
        data = value[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(data);
    }

    private static string NormalizeMessagesEndpoint(string? endpoint)
    {
        var trimmed = (endpoint ?? "https://api.anthropic.com/v1").TrimEnd('/');
        if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        else if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/responses".Length];
        return trimmed + "/messages";
    }

    private static string? ReadString(JsonObject? node, string propertyName)
        => node?[propertyName]?.GetValue<string>();

    private static int? ReadInt(JsonObject? node, string propertyName)
    {
        if (node?[propertyName] is not JsonValue value)
            return null;
        return value.TryGetValue<int>(out var intValue)
            ? intValue
            : value.TryGetValue<long>(out var longValue)
                ? checked((int)longValue)
                : null;
    }

    private static long ElapsedMilliseconds(long startedAt)
        => ElapsedMilliseconds(startedAt, Stopwatch.GetTimestamp());

    private static long ElapsedMilliseconds(long startedAt, long endedAt)
        => (long)((endedAt - startedAt) * 1000.0 / Stopwatch.Frequency);

    private sealed class AnthropicStreamParser
    {
        private readonly SortedDictionary<int, JsonObject> _contentBlocks = [];
        private readonly Dictionary<int, StringBuilder> _toolInputByBlock = [];
        private readonly Dictionary<int, int> _toolIndexByBlock = [];
        private readonly HashSet<string> _seenToolCallIds = new(StringComparer.Ordinal);
        private JsonObject? _usage;
        private bool _terminalEmitted;

        public IReadOnlyList<StreamDelta> Parse(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Invalid Anthropic streaming event.");
            var type = ReadString(root, "type");
            return type switch
            {
                "message_start" => ParseMessageStart(root),
                "content_block_start" => ParseContentBlockStart(root),
                "content_block_delta" => ParseContentBlockDelta(root),
                "content_block_stop" => ParseContentBlockStop(root),
                "message_delta" => ParseMessageDelta(root),
                "message_stop" => ParseMessageStop(),
                "error" => throw CreateStreamException(root),
                _ => [],
            };
        }

        private IReadOnlyList<StreamDelta> ParseMessageStart(JsonObject root)
        {
            _usage = (root["message"]?["usage"] as JsonObject)?.DeepClone().AsObject();
            return [];
        }

        private IReadOnlyList<StreamDelta> ParseContentBlockStart(JsonObject root)
        {
            var blockIndex = ReadInt(root, "index") ?? 0;
            if (root["content_block"] is not JsonObject sourceBlock)
                return [];

            var block = (JsonObject)sourceBlock.DeepClone();
            _contentBlocks[blockIndex] = block;
            switch (ReadString(block, "type"))
            {
                case "text":
                {
                    var text = ReadString(block, "text");
                    return string.IsNullOrEmpty(text) ? [] : [new StreamDelta { ContentDelta = text }];
                }
                case "thinking":
                {
                    var thinking = ReadString(block, "thinking");
                    return string.IsNullOrEmpty(thinking) ? [] : [new StreamDelta { ReasoningDelta = thinking }];
                }
                case "tool_use":
                {
                    var id = EnsureUniqueCallId(ReadString(block, "id"), _seenToolCallIds);
                    block["id"] = id;
                    var toolIndex = _toolIndexByBlock.Count;
                    _toolIndexByBlock[blockIndex] = toolIndex;
                    _toolInputByBlock[blockIndex] = new StringBuilder();
                    var initialInput = block["input"] as JsonObject;
                    var initialJson = initialInput is { Count: > 0 } ? initialInput.ToJsonString() : null;
                    if (initialJson is not null)
                        _toolInputByBlock[blockIndex].Append(initialJson);
                    return
                    [
                        new StreamDelta
                        {
                            ToolCallIndex = toolIndex,
                            ToolCallId = id,
                            ToolCallNameDelta = ReadString(block, "name"),
                            ToolCallArgsDelta = initialJson,
                        },
                    ];
                }
                default:
                    return [];
            }
        }

        private IReadOnlyList<StreamDelta> ParseContentBlockDelta(JsonObject root)
        {
            var blockIndex = ReadInt(root, "index") ?? 0;
            if (root["delta"] is not JsonObject delta)
                return [];

            switch (ReadString(delta, "type"))
            {
                case "text_delta":
                {
                    var text = ReadString(delta, "text");
                    AppendBlockString(blockIndex, "text", text);
                    return text is null ? [] : [new StreamDelta { ContentDelta = text }];
                }
                case "thinking_delta":
                {
                    var thinking = ReadString(delta, "thinking");
                    AppendBlockString(blockIndex, "thinking", thinking);
                    return thinking is null ? [] : [new StreamDelta { ReasoningDelta = thinking }];
                }
                case "signature_delta":
                    if (_contentBlocks.TryGetValue(blockIndex, out var thinkingBlock))
                        thinkingBlock["signature"] = ReadString(delta, "signature");
                    return [];
                case "input_json_delta":
                {
                    var partialJson = ReadString(delta, "partial_json") ?? string.Empty;
                    if (!_toolInputByBlock.TryGetValue(blockIndex, out var input))
                    {
                        input = new StringBuilder();
                        _toolInputByBlock[blockIndex] = input;
                    }
                    input.Append(partialJson);
                    return _toolIndexByBlock.TryGetValue(blockIndex, out var toolIndex)
                        ? [new StreamDelta { ToolCallIndex = toolIndex, ToolCallArgsDelta = partialJson }]
                        : [];
                }
                default:
                    return [];
            }
        }

        private IReadOnlyList<StreamDelta> ParseContentBlockStop(JsonObject root)
        {
            var blockIndex = ReadInt(root, "index") ?? 0;
            if (_contentBlocks.TryGetValue(blockIndex, out var block)
                && ReadString(block, "type") == "tool_use"
                && _toolInputByBlock.TryGetValue(blockIndex, out var input)
                && input.Length > 0)
            {
                block["input"] = JsonNode.Parse(input.ToString()) is JsonObject inputObject
                    ? inputObject
                    : throw new InvalidOperationException("Anthropic streamed tool input is not a JSON object.");
            }
            return [];
        }

        private IReadOnlyList<StreamDelta> ParseMessageDelta(JsonObject root)
        {
            if (root["usage"] is JsonObject deltaUsage)
            {
                _usage ??= new JsonObject();
                foreach (var property in deltaUsage)
                    _usage[property.Key] = property.Value?.DeepClone();
            }

            _terminalEmitted = true;
            return
            [
                new StreamDelta
                {
                    FinishReason = MapStopReason(ReadString(root["delta"] as JsonObject, "stop_reason")),
                    Usage = ParseUsage(_usage),
                    ContinuationState = BuildContinuation(),
                },
            ];
        }

        private IReadOnlyList<StreamDelta> ParseMessageStop()
        {
            if (_terminalEmitted)
                return [];
            _terminalEmitted = true;
            return
            [
                new StreamDelta
                {
                    FinishReason = "stop",
                    Usage = ParseUsage(_usage),
                    ContinuationState = BuildContinuation(),
                },
            ];
        }

        private LlmContinuationState BuildContinuation()
            => new(
                ProtocolName,
                _contentBlocks.Values.Select(block => block.ToJsonString()).ToList());

        private void AppendBlockString(int blockIndex, string propertyName, string? delta)
        {
            if (delta is null || !_contentBlocks.TryGetValue(blockIndex, out var block))
                return;
            block[propertyName] = (ReadString(block, propertyName) ?? string.Empty) + delta;
        }

        private static HttpRequestException CreateStreamException(JsonObject root)
        {
            var error = root["error"] as JsonObject;
            return new HttpRequestException(
                $"Anthropic Messages API error: {ReadString(error, "message") ?? "unknown provider error"}");
        }
    }
}
