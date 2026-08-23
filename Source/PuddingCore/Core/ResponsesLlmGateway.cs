using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PlatformLlmOptions = PuddingCode.Platform.Options.LlmOptions;

namespace PuddingCode.Core;

/// <summary>OpenAI Responses API gateway with buffered and streaming tool-call support.</summary>
public sealed class ResponsesLlmGateway(HttpClient httpClient, LlmOptions options) : ILlmGateway
{
    private const string ProtocolName = "responses";

    public IVisualArtifactResolver? VisualArtifactResolver { get; set; }
    public IAudioArtifactResolver? AudioArtifactResolver { get; set; }
    public string? WorkspaceId { get; set; }
    /// <summary>图片请求预算（ADR-077）；由 DirectLlmClient 按快照策略注入，默认产品上限。</summary>
    public VisionRequestPolicy? VisionPolicy { get; set; }

    public ResponsesLlmGateway(HttpClient httpClient, PlatformLlmOptions options)
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

    private readonly string _responsesEndpoint = NormalizeResponsesEndpoint(options.Endpoint);

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default)
    {
        var requestBody = await BuildResponsesRequestBodyAsync(messages, tools, stream: false, ct);
        using var request = CreateRequest(requestBody);
        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"LLM Responses API error ({response.StatusCode}): {json}",
                inner: null,
                response.StatusCode);
        }

        return ParseResponsesResponse(json);
    }

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = await BuildResponsesRequestBodyAsync(messages, tools, stream: true, ct);
        using var request = CreateRequest(requestBody);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LLM Responses API error ({response.StatusCode}): {errorJson}",
                inner: null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new ResponsesStreamParser();
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
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                yield break;

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
        var request = new HttpRequestMessage(HttpMethod.Post, _responsesEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return request;
    }

    private async Task<string> BuildResponsesRequestBodyAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        bool stream,
        CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            // Pudding replays output items itself. Do not retain provider-side application state.
            ["store"] = false,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
        };

        if (messages.Any(message =>
                message.Role == ChatRole.Tool
                && string.IsNullOrWhiteSpace(message.ToolCallId)))
        {
            throw new InvalidOperationException(
                "Responses function_call_output requires a non-empty call_id.");
        }

        var input = new JsonArray();
        var protocolSafeMessages = LlmMessageSequenceNormalizer.Normalize(messages).Messages;
        foreach (var message in protocolSafeMessages)
            await AddInputItemsAsync(input, message, ct);
        root["input"] = input;

        if (tools.Count > 0)
        {
            var toolNodes = new JsonArray();
            foreach (var tool in tools)
            {
                toolNodes.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = BuildParametersNode(tool),
                });
            }
            root["tools"] = toolNodes;
        }

        if (options.Temperature.HasValue)
            root["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            root["max_output_tokens"] = options.MaxTokens.Value;
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            root["reasoning"] = new JsonObject
            {
                ["effort"] = options.ReasoningEffort,
            };
        }

        return root.ToJsonString();
    }

    private async Task AddInputItemsAsync(JsonArray input, ChatMessage message, CancellationToken ct)
    {
        if (message.Role == ChatRole.Assistant
            && message.ContinuationState is { OutputItemsJson.Count: > 0 } continuation
            && string.Equals(continuation.Protocol, ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var itemJson in continuation.OutputItemsJson)
            {
                var item = JsonNode.Parse(itemJson)
                    ?? throw new InvalidOperationException("Responses continuation item is invalid JSON.");
                input.Add(item);
            }
            return;
        }

        if (message.Role == ChatRole.Tool)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
                throw new InvalidOperationException("Responses function_call_output requires a non-empty call_id.");

            var toolImageParts = ChatMessageMultimodalNormalizer.GetImageParts(message);
            JsonNode outputNode;
            if (toolImageParts.Count > 0)
            {
                // DeepSeek Responses 官方合同允许 input_image 出现在 function_call_output.output。
                // 只有明确支持图片型工具结果的协议才走该分支；不支持协议由各自 Gateway fail closed。
                var outputParts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(message.Content))
                    outputParts.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = message.Content,
                    });

                var plan = await PlanVisualInputsAsync(toolImageParts, ct);
                foreach (var image in plan.Images)
                    outputParts.Add(BuildInputImageNode(image));

                outputNode = outputParts;
            }
            else
            {
                outputNode = JsonValue.Create(message.Content ?? string.Empty)!;
            }

            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = message.ToolCallId,
                ["output"] = outputNode,
            });
            return;
        }

        if (message.Role == ChatRole.User)
        {
            var multimodalContent = await BuildMultimodalContentAsync(message, ct);
            JsonNode contentNode = (JsonNode?)multimodalContent
                ?? JsonValue.Create(message.Content ?? string.Empty)!;
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = contentNode,
            });
        }
        else if (!string.IsNullOrEmpty(message.Content) || message.Role != ChatRole.Assistant)
        {
            input.Add(new JsonObject
            {
                ["role"] = message.Role == ChatRole.System ? "system" : "assistant",
                ["content"] = message.Content ?? string.Empty,
            });
        }

        if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Id))
                    throw new InvalidOperationException("Responses function_call requires a non-empty call_id.");

                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson ?? string.Empty,
                });
            }
        }
    }

    private async Task<JsonArray?> BuildMultimodalContentAsync(ChatMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
            return null;

        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = message.Content,
            });
        }

        var imageParts = ChatMessageMultimodalNormalizer.GetImageParts(message);
        // ADR-077 §5.5：无视觉解析通道 = 文本路由；图片部件不进入请求（文本模型的附件
        // 以消息正文 artifact:// 占位为准）。有解析通道而解析失败才由 Planner fail closed。
        if (imageParts.Count > 0 && VisualArtifactResolver is not null)
        {
            var plan = await PlanVisualInputsAsync(imageParts, ct);
            foreach (var image in plan.Images)
                content.Add(BuildInputImageNode(image));
        }

        if (message.AudioArtifactIds is { Count: > 0 } && AudioArtifactResolver is not null)
        {
            foreach (var artifactId in message.AudioArtifactIds)
            {
                try
                {
                    var resolved = await AudioArtifactResolver.ResolveAsync(WorkspaceId!, artifactId, ct);
                    if (resolved is not null)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = new JsonObject
                            {
                                ["data"] = ExtractDataPayload(resolved.DataUri),
                                ["format"] = resolved.Format,
                            },
                        });
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Debug.WriteLine(
                        $"[ResponsesLlmGateway] Failed to resolve audio artifact '{artifactId}': {exception.Message}");
                }
            }
        }

        return content.Count > (string.IsNullOrWhiteSpace(message.Content) ? 0 : 1)
            ? content
            : null;
    }

    private async Task<VisualInputPlan> PlanVisualInputsAsync(
        IReadOnlyList<LlmImagePart> imageParts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId) || VisualArtifactResolver is null)
            throw new VisionPipelineException(
                VisionErrorCodes.ModelCapabilityMismatch,
                "Visual inputs require a workspace and a vision-capable route.");

        return await LlmVisualInputPlanner.PlanAsync(
            WorkspaceId!,
            imageParts,
            VisualArtifactResolver,
            VisionPolicy,
            ct);
    }

    /// <summary>canonical detail → DeepSeek Responses detail（original 等价 high）。</summary>
    private static JsonObject BuildInputImageNode(PlannedVisualInput image) => new()
    {
        ["type"] = "input_image",
        ["image_url"] = image.DataUri,
        ["detail"] = string.Equals(image.Detail, VisionContentPartDetails.Low, StringComparison.Ordinal)
            ? "low"
            : "high",
    };

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

    private static string ExtractDataPayload(string dataUri)
    {
        var commaIndex = dataUri.IndexOf(',');
        return dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
               && commaIndex >= 0
            ? dataUri[(commaIndex + 1)..]
            : dataUri;
    }

    private static LlmResponse ParseResponsesResponse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Invalid Responses API response: body is not a JSON object.");
        ThrowIfTerminalFailure(root);

        var output = root["output"] as JsonArray
            ?? throw new InvalidOperationException("Invalid Responses API response: output is missing.");
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var continuationItems = new List<string>(output.Count);
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var isIncomplete = ReadString(root, "status") == "incomplete";

        foreach (var sourceItem in output)
        {
            if (sourceItem is not JsonObject sourceObject)
                continue;

            var item = (JsonObject)sourceObject.DeepClone();
            switch (ReadString(item, "type"))
            {
                case "message":
                    AppendMessageContent(item["content"] as JsonArray, content);
                    break;
                case "reasoning":
                    AppendReasoningContent(item, reasoning);
                    break;
                case "function_call":
                {
                    var callId = EnsureUniqueCallId(ReadString(item, "call_id"), seenCallIds);
                    item["call_id"] = callId;
                    // A length-truncated function call may contain incomplete JSON arguments.
                    // Keep it replayable, but never expose it for execution in this turn.
                    if (!isIncomplete)
                    {
                        toolCalls.Add(new ToolCall(
                            callId,
                            ReadString(item, "name") ?? string.Empty,
                            ReadString(item, "arguments") ?? string.Empty));
                    }
                    break;
                }
            }
            continuationItems.Add(item.ToJsonString());
        }

        return new LlmResponse(
            content.ToString(),
            toolCalls.Count > 0 ? toolCalls : null,
            reasoning.Length > 0 ? reasoning.ToString() : null,
            ParseResponsesUsage(root["usage"]),
            new LlmContinuationState(ProtocolName, continuationItems));
    }

    private static void AppendMessageContent(JsonArray? parts, StringBuilder content)
    {
        if (parts is null)
            return;

        foreach (var part in parts.OfType<JsonObject>())
        {
            var type = ReadString(part, "type");
            if (type == "output_text")
                content.Append(ReadString(part, "text"));
            else if (type == "refusal")
                content.Append(ReadString(part, "refusal"));
        }
    }

    private static void AppendReasoningSummary(JsonArray? parts, StringBuilder reasoning)
    {
        if (parts is null)
            return;
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (ReadString(part, "type") is "summary_text")
                reasoning.Append(ReadString(part, "text"));
        }
    }

    private static void AppendReasoningContent(JsonObject item, StringBuilder reasoning)
    {
        if (item["content"] is JsonArray content)
        {
            foreach (var part in content.OfType<JsonObject>())
            {
                if (ReadString(part, "type") is "reasoning_text")
                    reasoning.Append(ReadString(part, "text"));
            }
        }

        // OpenAI reasoning summaries and DeepSeek plaintext reasoning are different
        // Responses API shapes. Preserve both when a provider returns them together.
        AppendReasoningSummary(item["summary"] as JsonArray, reasoning);
    }

    private static TokenUsageDto? ParseResponsesUsage(JsonNode? usageNode)
    {
        if (usageNode is not JsonObject usage)
            return null;

        var inputTokens = ReadInt(usage, "input_tokens");
        var cachedTokens = ReadInt(usage["input_tokens_details"] as JsonObject, "cached_tokens");
        return new TokenUsageDto
        {
            PromptTokens = inputTokens,
            CompletionTokens = ReadInt(usage, "output_tokens"),
            TotalTokens = ReadInt(usage, "total_tokens"),
            PromptCacheHitTokens = cachedTokens,
            PromptCacheMissTokens = inputTokens.HasValue && cachedTokens.HasValue
                ? Math.Max(0, inputTokens.Value - cachedTokens.Value)
                : null,
        };
    }

    private static void ThrowIfTerminalFailure(JsonObject response)
    {
        var status = ReadString(response, "status");
        if (status == "failed")
        {
            var error = response["error"] as JsonObject;
            throw new HttpRequestException(
                $"Responses API failed: {ReadString(error, "message") ?? "unknown provider error"}");
        }

        // `incomplete` is a successful HTTP terminal state with partial output, usage,
        // and replayable output items. The caller must receive that data instead of a
        // synthetic provider failure (most commonly the model reached max_output_tokens).
    }

    private static string EnsureUniqueCallId(string? providerCallId, HashSet<string> seenCallIds)
    {
        var callId = providerCallId;
        if (!string.IsNullOrWhiteSpace(callId) && seenCallIds.Add(callId))
            return callId;

        do
        {
            callId = CreateSyntheticCallId();
        }
        while (!seenCallIds.Add(callId));
        return callId;
    }

    private static string CreateSyntheticCallId()
        => $"call_pudding_{Guid.NewGuid():N}"[..40];

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

    private static string NormalizeResponsesEndpoint(string? endpoint)
    {
        var trimmed = (endpoint ?? "https://api.openai.com/v1").TrimEnd('/');
        if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        return trimmed + "/responses";
    }

    private static long ElapsedMilliseconds(long startedAt)
        => ElapsedMilliseconds(startedAt, Stopwatch.GetTimestamp());

    private static long ElapsedMilliseconds(long startedAt, long endedAt)
        => (long)((endedAt - startedAt) * 1000.0 / Stopwatch.Frequency);

    private sealed class ResponsesStreamParser
    {
        private readonly Dictionary<string, ToolStreamState> _toolsByItemId = new(StringComparer.Ordinal);
        private readonly Dictionary<int, ToolStreamState> _toolsByOutputIndex = [];
        private readonly List<ToolStreamState> _tools = [];
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);

        public IReadOnlyList<StreamDelta> Parse(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new HttpRequestException("Invalid Responses API stream event JSON.");
            var eventType = ReadString(root, "type");

            return eventType switch
            {
                "response.output_text.delta" => TextDelta(root, "delta"),
                "response.refusal.delta" => TextDelta(root, "delta"),
                "response.reasoning_text.delta" => ReasoningDelta(root),
                "response.reasoning_summary_text.delta" => ReasoningDelta(root),
                "response.output_item.added" => OutputItemAdded(root),
                "response.output_item.done" => OutputItemDone(root),
                "response.function_call_arguments.delta" => FunctionArgumentsDelta(root),
                "response.function_call_arguments.done" => FunctionArgumentsDone(root),
                "response.completed" => ResponseCompleted(root),
                "response.failed" => throw CreateTerminalException(root, "failed"),
                "response.incomplete" => ResponseIncomplete(root),
                "error" => throw CreateErrorEventException(root),
                _ => [],
            };
        }

        private static IReadOnlyList<StreamDelta> TextDelta(JsonObject root, string propertyName)
        {
            var delta = ReadString(root, propertyName);
            return string.IsNullOrEmpty(delta) ? [] : [new StreamDelta { ContentDelta = delta }];
        }

        private static IReadOnlyList<StreamDelta> ReasoningDelta(JsonObject root)
        {
            var delta = ReadString(root, "delta");
            return string.IsNullOrEmpty(delta) ? [] : [new StreamDelta { ReasoningDelta = delta }];
        }

        private IReadOnlyList<StreamDelta> OutputItemAdded(JsonObject root)
        {
            if (root["item"] is not JsonObject item || ReadString(item, "type") != "function_call")
                return [];

            var state = GetOrCreateState(root, item);
            var name = ReadString(item, "name");
            if (!string.IsNullOrEmpty(name))
                state.Name = name;
            state.NameEmitted = !string.IsNullOrEmpty(state.Name);

            return
            [
                new StreamDelta
                {
                    ToolCallIndex = state.Index,
                    ToolCallId = state.CallId,
                    ToolCallIdWasSynthesized = state.CallIdWasSynthesized,
                    ToolCallNameDelta = state.Name,
                },
            ];
        }

        private IReadOnlyList<StreamDelta> FunctionArgumentsDelta(JsonObject root)
        {
            var state = GetOrCreateState(root, item: null);
            var delta = ReadString(root, "delta") ?? string.Empty;
            state.Arguments.Append(delta);
            state.SawArgumentDelta = true;
            return
            [
                new StreamDelta
                {
                    ToolCallIndex = state.Index,
                    ToolCallId = state.CallId,
                    ToolCallIdWasSynthesized = state.CallIdWasSynthesized,
                    ToolCallArgsDelta = delta,
                },
            ];
        }

        private IReadOnlyList<StreamDelta> FunctionArgumentsDone(JsonObject root)
        {
            var state = GetOrCreateState(root, item: null);
            var name = ReadString(root, "name");
            var nameDelta = !state.NameEmitted && !string.IsNullOrEmpty(name) ? name : null;
            if (!string.IsNullOrEmpty(name))
                state.Name = name;
            state.NameEmitted |= nameDelta is not null;

            var arguments = ReadString(root, "arguments");
            var argumentsDelta = !state.SawArgumentDelta && state.Arguments.Length == 0
                ? arguments
                : null;
            if (!string.IsNullOrEmpty(argumentsDelta))
                state.Arguments.Append(argumentsDelta);

            return
            [
                new StreamDelta
                {
                    ToolCallIndex = state.Index,
                    ToolCallId = state.CallId,
                    ToolCallIdWasSynthesized = state.CallIdWasSynthesized,
                    ToolCallNameDelta = nameDelta,
                    ToolCallArgsDelta = argumentsDelta,
                    FinishReason = "tool_calls",
                },
            ];
        }

        private IReadOnlyList<StreamDelta> OutputItemDone(JsonObject root)
        {
            if (root["item"] is not JsonObject item || ReadString(item, "type") != "function_call")
                return [];

            var state = GetOrCreateState(root, item);
            var name = ReadString(item, "name");
            var nameDelta = !state.NameEmitted && !string.IsNullOrEmpty(name) ? name : null;
            if (!string.IsNullOrEmpty(name))
                state.Name = name;
            state.NameEmitted |= nameDelta is not null;

            var arguments = ReadString(item, "arguments");
            var argumentsDelta = !state.SawArgumentDelta && state.Arguments.Length == 0
                ? arguments
                : null;
            if (!string.IsNullOrEmpty(argumentsDelta))
                state.Arguments.Append(argumentsDelta);

            if (nameDelta is null && argumentsDelta is null)
                return [];

            return
            [
                new StreamDelta
                {
                    ToolCallIndex = state.Index,
                    ToolCallId = state.CallId,
                    ToolCallIdWasSynthesized = state.CallIdWasSynthesized,
                    ToolCallNameDelta = nameDelta,
                    ToolCallArgsDelta = argumentsDelta,
                },
            ];
        }

        private IReadOnlyList<StreamDelta> ResponseCompleted(JsonObject root)
            => ResponseTerminal(root, incomplete: false);

        private IReadOnlyList<StreamDelta> ResponseIncomplete(JsonObject root)
            => ResponseTerminal(root, incomplete: true);

        private IReadOnlyList<StreamDelta> ResponseTerminal(JsonObject root, bool incomplete)
        {
            var response = root["response"] as JsonObject ?? root;
            ThrowIfTerminalFailure(response);
            var continuation = CreateContinuation(response["output"] as JsonArray);
            var hasToolCalls = (response["output"] as JsonArray)?
                .OfType<JsonObject>()
                .Any(item => ReadString(item, "type") == "function_call") == true;

            return
            [
                new StreamDelta
                {
                    Usage = ParseResponsesUsage(response["usage"]),
                    ContinuationState = continuation,
                    FinishReason = incomplete
                        ? MapIncompleteReason(response)
                        : hasToolCalls ? "tool_calls" : "stop",
                },
            ];
        }

        private static string MapIncompleteReason(JsonObject response)
        {
            var details = response["incomplete_details"] as JsonObject;
            return ReadString(details, "reason") == "max_output_tokens"
                ? "length"
                : "incomplete";
        }

        private LlmContinuationState? CreateContinuation(JsonArray? output)
        {
            if (output is null)
                return null;

            var items = new List<string>(output.Count);
            for (var outputIndex = 0; outputIndex < output.Count; outputIndex++)
            {
                if (output[outputIndex] is not JsonObject sourceItem)
                    continue;
                var item = (JsonObject)sourceItem.DeepClone();
                if (ReadString(item, "type") == "function_call"
                    && TryFindState(ReadString(item, "id"), outputIndex, out var state))
                {
                    item["call_id"] = state.CallId;
                    if (string.IsNullOrWhiteSpace(ReadString(item, "name")) && !string.IsNullOrWhiteSpace(state.Name))
                        item["name"] = state.Name;
                    if (string.IsNullOrWhiteSpace(ReadString(item, "arguments")) && state.Arguments.Length > 0)
                        item["arguments"] = state.Arguments.ToString();
                }
                items.Add(item.ToJsonString());
            }
            return new LlmContinuationState(ProtocolName, items);
        }

        private ToolStreamState GetOrCreateState(JsonObject root, JsonObject? item)
        {
            var itemId = ReadString(root, "item_id") ?? ReadString(item, "id");
            var outputIndex = ReadInt(root, "output_index");
            if (TryFindState(itemId, outputIndex, out var existing))
                return existing;

            var callId = ReadString(item, "call_id");
            var synthesized = string.IsNullOrWhiteSpace(callId) || !_callIds.Add(callId);
            if (synthesized)
            {
                do
                {
                    callId = CreateSyntheticCallId();
                }
                while (!_callIds.Add(callId));
            }

            var state = new ToolStreamState(
                _tools.Count,
                itemId,
                outputIndex,
                callId!,
                synthesized,
                ReadString(item, "name") ?? string.Empty);
            _tools.Add(state);
            if (!string.IsNullOrWhiteSpace(itemId))
                _toolsByItemId[itemId] = state;
            if (outputIndex.HasValue)
                _toolsByOutputIndex[outputIndex.Value] = state;
            return state;
        }

        private bool TryFindState(string? itemId, int? outputIndex, out ToolStreamState state)
        {
            if (!string.IsNullOrWhiteSpace(itemId) && _toolsByItemId.TryGetValue(itemId, out state!))
                return true;
            if (outputIndex.HasValue && _toolsByOutputIndex.TryGetValue(outputIndex.Value, out state!))
                return true;
            state = null!;
            return false;
        }

        private static HttpRequestException CreateTerminalException(JsonObject root, string status)
        {
            var response = root["response"] as JsonObject ?? root;
            if (status == "failed")
            {
                var error = response["error"] as JsonObject;
                return new HttpRequestException(
                    $"Responses API failed: {ReadString(error, "message") ?? "unknown provider error"}");
            }

            var details = response["incomplete_details"] as JsonObject;
            return new HttpRequestException(
                $"Responses API incomplete: {ReadString(details, "reason") ?? "unknown reason"}");
        }

        private static HttpRequestException CreateErrorEventException(JsonObject root)
        {
            var error = root["error"] as JsonObject;
            return new HttpRequestException(
                $"Responses API stream error: {ReadString(error, "message") ?? ReadString(root, "message") ?? "unknown provider error"}");
        }

        private sealed class ToolStreamState(
            int index,
            string? itemId,
            int? outputIndex,
            string callId,
            bool callIdWasSynthesized,
            string name)
        {
            public int Index { get; } = index;
            public string? ItemId { get; } = itemId;
            public int? OutputIndex { get; } = outputIndex;
            public string CallId { get; } = callId;
            public bool CallIdWasSynthesized { get; } = callIdWasSynthesized;
            public string Name { get; set; } = name;
            public bool NameEmitted { get; set; }
            public bool SawArgumentDelta { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
