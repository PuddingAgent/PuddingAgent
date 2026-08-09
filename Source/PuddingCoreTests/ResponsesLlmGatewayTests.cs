using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Platform;

namespace PuddingCoreTests;

[TestClass]
public sealed class ResponsesLlmGatewayTests
{
    [TestMethod]
    public async Task ChatAsync_BasicRequest_UsesResponsesStatePolicy()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        var gateway = CreateGateway(request =>
        {
            requestUri = request.RequestUri;
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.System, "be concise"), new ChatMessage(ChatRole.User, "hello")],
            []);

        var body = JsonNode.Parse(requestBody!)!.AsObject();
        Assert.AreEqual("/v1/responses", requestUri!.AbsolutePath);
        Assert.AreEqual("gpt-5.6-luna", body["model"]!.GetValue<string>());
        Assert.IsFalse(body["stream"]!.GetValue<bool>());
        Assert.IsFalse(body["store"]!.GetValue<bool>());
        Assert.AreEqual("reasoning.encrypted_content", body["include"]![0]!.GetValue<string>());
        Assert.AreEqual("system", body["input"]![0]!["role"]!.GetValue<string>());
        Assert.AreEqual("user", body["input"]![1]!["role"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ToolRound_UsesFunctionCallAndFunctionCallOutputItems()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });

        await gateway.ChatAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    null,
                    ToolCalls: [new ToolCall("call_1", "get_weather", "{\"city\":\"Beijing\"}")]),
                new ChatMessage(ChatRole.Tool, "sunny", ToolCallId: "call_1"),
            ],
            []);

        var input = JsonNode.Parse(requestBody!)!["input"]!.AsArray();
        Assert.AreEqual(2, input.Count);
        Assert.AreEqual("function_call", input[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("call_1", input[0]!["call_id"]!.GetValue<string>());
        Assert.AreEqual("get_weather", input[0]!["name"]!.GetValue<string>());
        Assert.AreEqual("function_call_output", input[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("call_1", input[1]!["call_id"]!.GetValue<string>());
        Assert.AreEqual("sunny", input[1]!["output"]!.GetValue<string>());
        Assert.IsNull(input[1]!["role"]);
    }

    [TestMethod]
    public async Task ChatAsync_Continuation_ReplaysAllOutputItemsWithoutDuplicatingToolCall()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        var continuation = new LlmContinuationState(
            "responses",
            [
                """{"type":"reasoning","id":"rs_1","encrypted_content":"opaque"}""",
                """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{}"}""",
            ]);

        await gateway.ChatAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    null,
                    ToolCalls: [new ToolCall("call_1", "lookup", "{}")],
                    ContinuationState: continuation),
                new ChatMessage(ChatRole.Tool, "result", ToolCallId: "call_1"),
            ],
            []);

        var input = JsonNode.Parse(requestBody!)!["input"]!.AsArray();
        Assert.AreEqual(3, input.Count);
        Assert.AreEqual("reasoning", input[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("opaque", input[0]!["encrypted_content"]!.GetValue<string>());
        Assert.AreEqual("function_call", input[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("function_call_output", input[2]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ToolOutputWithoutCallId_IsRejected()
    {
        var gateway = CreateGateway(_ => OkResponsesResponse());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gateway.ChatAsync(
            [new ChatMessage(ChatRole.Tool, "result")],
            []));
    }

    [TestMethod]
    public async Task ChatAsync_Tools_AreFlatResponsesFunctionDefinitions()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        var tool = new StubTool(
            "search_docs",
            "Search documentation",
            new ToolParameterSchema(
                [new ToolParameter("query", "string", "Search term")],
                ["query"]));

        await gateway.ChatAsync([new ChatMessage(ChatRole.User, "find it")], [tool]);

        var definition = JsonNode.Parse(requestBody!)!["tools"]![0]!.AsObject();
        Assert.AreEqual("function", definition["type"]!.GetValue<string>());
        Assert.AreEqual("search_docs", definition["name"]!.GetValue<string>());
        Assert.AreEqual("Search documentation", definition["description"]!.GetValue<string>());
        Assert.AreEqual("object", definition["parameters"]!["type"]!.GetValue<string>());
        Assert.IsNull(definition["function"], "Responses tools must not use the Chat Completions function wrapper.");
    }

    [TestMethod]
    public async Task ChatAsync_RawToolSchema_IsPreserved()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"n":{"type":"integer"}},"additionalProperties":false}""")
            .RootElement.Clone();

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "run")],
            [new StubTool("raw", "raw", new ToolParameterSchema([], [], schema))]);

        var parameters = JsonNode.Parse(requestBody!)!["tools"]![0]!["parameters"]!;
        Assert.AreEqual("integer", parameters["properties"]!["n"]!["type"]!.GetValue<string>());
        Assert.IsFalse(parameters["additionalProperties"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ChatAsync_MultimodalInput_UsesResponsesContentPartsAndRawAudioBase64()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new FixedVisualResolver();
        gateway.AudioArtifactResolver = new FixedAudioResolver();

        await gateway.ChatAsync(
            [new ChatMessage(
                ChatRole.User,
                "describe",
                VisualArtifactIds: ["vision-1"],
                AudioArtifactIds: ["audio-1"])],
            []);

        var content = JsonNode.Parse(requestBody!)!["input"]![0]!["content"]!.AsArray();
        Assert.AreEqual("input_text", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("input_image", content[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", content[1]!["image_url"]!.GetValue<string>());
        Assert.AreEqual("input_audio", content[2]!["type"]!.GetValue<string>());
        Assert.AreEqual("UklGRg==", content[2]!["input_audio"]!["data"]!.GetValue<string>());
        Assert.AreEqual("wav", content[2]!["input_audio"]!["format"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_OptionalGenerationFields_UseResponsesNames()
    {
        string? requestBody = null;
        var gateway = CreateGateway(
            request =>
            {
                requestBody = ReadBody(request);
                return OkResponsesResponse();
            },
            new LlmOptions("https://provider.example/v1", "key", "gpt-5", 0.2, 2048, "high"));

        await gateway.ChatAsync([new ChatMessage(ChatRole.User, "hi")], []);

        var body = JsonNode.Parse(requestBody!)!;
        Assert.AreEqual(0.2, body["temperature"]!.GetValue<double>());
        Assert.AreEqual(2048, body["max_output_tokens"]!.GetValue<int>());
        Assert.AreEqual("high", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.IsNull(body["max_tokens"]);
    }

    [TestMethod]
    public async Task ChatAsync_ParsesTextReasoningToolsUsageAndContinuation()
    {
        const string responseJson = """
            {
              "status":"completed",
              "output":[
                {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"plan"}],"encrypted_content":"opaque"},
                {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"answer"}]},
                {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"q\":1}"}
              ],
              "usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":40},"output_tokens":25,"total_tokens":125}
            }
            """;
        var gateway = CreateGateway(_ => JsonResponse(responseJson));

        var response = await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []);

        Assert.AreEqual("answer", response.Content);
        Assert.AreEqual("plan", response.ReasoningContent);
        var toolCall = response.ToolCalls!.Single();
        Assert.AreEqual("call_1", toolCall.Id);
        Assert.AreEqual("lookup", toolCall.Name);
        Assert.AreEqual(100, response.Usage!.PromptTokens);
        Assert.AreEqual(25, response.Usage.CompletionTokens);
        Assert.AreEqual(125, response.Usage.TotalTokens);
        Assert.AreEqual(40, response.Usage.PromptCacheHitTokens);
        Assert.AreEqual(60, response.Usage.PromptCacheMissTokens);
        Assert.AreEqual(3, response.ContinuationState!.OutputItemsJson.Count);
        StringAssert.Contains(response.ContinuationState.OutputItemsJson[0], "opaque");
    }

    [TestMethod]
    public async Task ChatAsync_Refusal_IsReturnedAsVisibleContent()
    {
        var gateway = CreateGateway(_ => JsonResponse(
            """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"cannot comply"}]}]}"""));

        var response = await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []);

        Assert.AreEqual("cannot comply", response.Content);
    }

    [TestMethod]
    public async Task ChatAsync_MissingCallId_GeneratesReplayableId()
    {
        var gateway = CreateGateway(_ => JsonResponse(
            """{"status":"completed","output":[{"type":"function_call","id":"fc_1","name":"lookup","arguments":"{}"}]}"""));

        var response = await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []);

        var callId = response.ToolCalls!.Single().Id;
        StringAssert.StartsWith(callId, "call_pudding_");
        Assert.AreEqual(
            callId,
            JsonNode.Parse(response.ContinuationState!.OutputItemsJson.Single())!["call_id"]!.GetValue<string>());
    }

    [TestMethod]
    public void ContextUsageEstimator_CountsOpaqueContinuationInsteadOfDuplicateAssistantProjection()
    {
        var store = new ContextUsageSnapshotStore();
        var continuation = new LlmContinuationState(
            "responses",
            [$$"""{"type":"reasoning","encrypted_content":"{{new string('x', 4096)}}"}"""]);

        var snapshot = store.CaptureLlmRequest(
            "session-1",
            [new ChatMessage(
                ChatRole.Assistant,
                "short",
                ToolCalls: [new ToolCall("call_1", "lookup", "{}")],
                ContinuationState: continuation)],
            tools: null,
            modelId: "gpt-5");

        Assert.IsGreaterThan(100, snapshot.MessageTokens);
    }

    [TestMethod]
    [DataRow("failed", "provider exploded")]
    [DataRow("incomplete", "max_output_tokens")]
    public async Task ChatAsync_NonCompletedTerminalStatus_Throws(string status, string detail)
    {
        var json = status == "failed"
            ? $$"""{"status":"failed","error":{"message":"{{detail}}"},"output":[]}"""
            : $$"""{"status":"incomplete","incomplete_details":{"reason":"{{detail}}"},"output":[]}""";
        var gateway = CreateGateway(_ => JsonResponse(json));

        var exception = await ThrowsHttpRequestExceptionAsync(
            () => gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []));

        StringAssert.Contains(exception.Message, detail);
    }

    [TestMethod]
    public async Task ChatAsync_MalformedSuccessWithoutOutput_Throws()
    {
        var gateway = CreateGateway(_ => JsonResponse("""{"status":"completed","text":"not-an-official-shape"}"""));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []));
    }

    [TestMethod]
    public async Task ChatAsync_HttpError_PreservesStatusCode()
    {
        var gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"invalid"}}"""),
        });

        var exception = await ThrowsHttpRequestExceptionAsync(
            () => gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []));

        Assert.AreEqual(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [TestMethod]
    public async Task ChatStreamAsync_OfficialToolEvents_KeepCompactIndexAndContinuation()
    {
        const string sse = """
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"{\"city\":"}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"\"Beijing\"}"}

            data: {"type":"response.function_call_arguments.done","item_id":"fc_1","output_index":1,"name":"get_weather","arguments":"{\"city\":\"Beijing\"}"}

            data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"reasoning","id":"rs_1","encrypted_content":"opaque"},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Beijing\"}"}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => SseResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.AreEqual(5, deltas.Count);
        Assert.AreEqual(0, deltas[0].ToolCallIndex, "Provider output_index includes reasoning items and must not be used as Runtime tool index.");
        Assert.AreEqual("call_1", deltas[0].ToolCallId);
        Assert.AreEqual("get_weather", deltas[0].ToolCallNameDelta);
        Assert.AreEqual("{\"city\":", deltas[1].ToolCallArgsDelta);
        Assert.AreEqual("\"Beijing\"}", deltas[2].ToolCallArgsDelta);
        Assert.IsNull(deltas[3].ToolCallArgsDelta, "The done event must not duplicate arguments already delivered as deltas.");
        Assert.AreEqual("tool_calls", deltas[3].FinishReason);
        Assert.AreEqual(15, deltas[4].Usage!.TotalTokens);
        Assert.AreEqual("tool_calls", deltas[4].FinishReason);
        Assert.AreEqual(2, deltas[4].ContinuationState!.OutputItemsJson.Count);
    }

    [TestMethod]
    public async Task ChatStreamAsync_MultipleTools_MapsSparseOutputIndexesToDenseToolIndexes()
    {
        const string sse = """
            data: {"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_a","call_id":"call_a","name":"first","arguments":""}}

            data: {"type":"response.output_item.added","output_index":5,"item":{"type":"function_call","id":"fc_b","call_id":"call_a","name":"second","arguments":""}}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => SseResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        CollectionAssert.AreEqual(new[] { 0, 1 }, deltas.Select(delta => delta.ToolCallIndex!.Value).ToArray());
        Assert.AreEqual("call_a", deltas[0].ToolCallId);
        StringAssert.StartsWith(deltas[1].ToolCallId, "call_pudding_");
        Assert.IsTrue(deltas[1].ToolCallIdWasSynthesized);
    }

    [TestMethod]
    public async Task ChatStreamAsync_CompletedText_EmitsUsageContinuationAndStop()
    {
        const string sse = """
            data:{"type":"response.output_text.delta","delta":"hello"}

            data:{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"hello"}]}],"usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4}}}

            data:[DONE]

            """;
        var gateway = CreateGateway(_ => SseResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.AreEqual("hello", deltas[0].ContentDelta);
        Assert.AreEqual(1L, deltas[0].ProviderChunkIndex);
        Assert.AreEqual("stop", deltas[1].FinishReason);
        Assert.AreEqual(4, deltas[1].Usage!.TotalTokens);
        Assert.AreEqual(1, deltas[1].ContinuationState!.OutputItemsJson.Count);
    }

    [TestMethod]
    [DataRow("error")]
    [DataRow("response.failed")]
    [DataRow("response.incomplete")]
    public async Task ChatStreamAsync_TerminalErrorEvents_Throw(string eventType)
    {
        var payload = eventType switch
        {
            "error" => """{"type":"error","error":{"message":"rate limited"}}""",
            "response.failed" => """{"type":"response.failed","response":{"status":"failed","error":{"message":"provider failed"}}}""",
            _ => """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}""",
        };
        var gateway = CreateGateway(_ => SseResponse($"data: {payload}\n\ndata: [DONE]\n\n"));

        await ThrowsHttpRequestExceptionAsync(async () =>
        {
            await foreach (var _ in gateway.ChatStreamAsync(
                               [new ChatMessage(ChatRole.User, "inspect")],
                               []))
            {
            }
        });
    }

    [TestMethod]
    public async Task ChatStreamAsync_HttpError_PreservesStatusCode()
    {
        var gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"slow down"}}"""),
        });

        var exception = await ThrowsHttpRequestExceptionAsync(async () =>
        {
            await foreach (var _ in gateway.ChatStreamAsync(
                               [new ChatMessage(ChatRole.User, "inspect")],
                               []))
            {
            }
        });

        Assert.AreEqual(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [TestMethod]
    [DataRow("https://provider.example/v1", "/v1/responses")]
    [DataRow("https://provider.example/v1/responses", "/v1/responses")]
    [DataRow("https://provider.example/v1/chat/completions", "/v1/responses")]
    public async Task EndpointNormalization_PostsToResponses(string endpoint, string expectedPath)
    {
        Uri? requestUri = null;
        var gateway = CreateGateway(
            request =>
            {
                requestUri = request.RequestUri;
                return OkResponsesResponse();
            },
            new LlmOptions(endpoint, "key", "model"));

        await gateway.ChatAsync([new ChatMessage(ChatRole.User, "hello")], []);

        Assert.AreEqual(expectedPath, requestUri!.AbsolutePath);
    }

    private static async Task<List<StreamDelta>> ReadStreamAsync(ResponsesLlmGateway gateway)
    {
        var deltas = new List<StreamDelta>();
        await foreach (var delta in gateway.ChatStreamAsync(
                           [new ChatMessage(ChatRole.User, "inspect")],
                           []))
        {
            deltas.Add(delta);
        }
        return deltas;
    }

    private static async Task<HttpRequestException> ThrowsHttpRequestExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HttpRequestException exception)
        {
            return exception;
        }

        Assert.Fail("Expected HttpRequestException.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static ResponsesLlmGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        LlmOptions? options = null)
        => new(
            new HttpClient(new StubHttpMessageHandler(send)),
            options ?? new LlmOptions("https://provider.example/v1", "test-key", "gpt-5.6-luna"));

    private static string ReadBody(HttpRequestMessage request)
        => request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage OkResponsesResponse()
        => JsonResponse(
            """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""");

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage SseResponse(string sse)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };

    private sealed class StubTool(string name, string description, ToolParameterSchema parameters) : ITool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public ToolParameterSchema Parameters { get; } = parameters;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => Task.FromResult("ok");
    }

    private sealed class FixedVisualResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<VisualArtifactResolveResult?>(new(
                artifactId,
                "data:image/png;base64,iVBORw0KGgo=",
                "image/png"));
    }

    private sealed class FixedAudioResolver : IAudioArtifactResolver
    {
        public Task<AudioArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<AudioArtifactResolveResult?>(new(
                artifactId,
                "data:audio/wav;base64,UklGRg==",
                "audio/wav",
                "wav"));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
