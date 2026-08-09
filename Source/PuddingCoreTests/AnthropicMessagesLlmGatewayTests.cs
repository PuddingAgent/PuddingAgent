using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Platform;

namespace PuddingCoreTests;

[TestClass]
public sealed class AnthropicMessagesLlmGatewayTests
{
    [TestMethod]
    public async Task ChatAsync_BasicRequest_UsesMessagesEndpointHeadersAndTopLevelSystem()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        HttpRequestHeadersSnapshot? headers = null;
        var gateway = CreateGateway(request =>
        {
            requestUri = request.RequestUri;
            requestBody = ReadBody(request);
            headers = new HttpRequestHeadersSnapshot(
                request.Headers.GetValues("x-api-key").Single(),
                request.Headers.GetValues("anthropic-version").Single(),
                request.Headers.Authorization?.Scheme);
            return OkResponse();
        });

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.System, "be concise"), new ChatMessage(ChatRole.User, "hello")],
            []);

        var body = JsonNode.Parse(requestBody!)!.AsObject();
        Assert.AreEqual("/v1/messages", requestUri!.AbsolutePath);
        Assert.AreEqual("test-key", headers!.ApiKey);
        Assert.AreEqual("2023-06-01", headers.Version);
        Assert.IsNull(headers.AuthorizationScheme);
        Assert.AreEqual("qwen3.8-max", body["model"]!.GetValue<string>());
        Assert.AreEqual(131072, body["max_tokens"]!.GetValue<int>());
        Assert.AreEqual("be concise", body["system"]!.GetValue<string>());
        Assert.AreEqual("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.AreEqual("hello", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ToolRound_UsesToolUseAndGroupedToolResults()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponse();
        });
        var tools = new ITool[]
        {
            new StubTool(
                "lookup",
                "Lookup a value",
                new ToolParameterSchema(
                    [new ToolParameter("query", "string", "Query")],
                    ["query"])),
        };

        await gateway.ChatAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    "checking",
                    ToolCalls:
                    [
                        new ToolCall("toolu_1", "lookup", "{\"query\":\"a\"}"),
                        new ToolCall("toolu_2", "lookup", "{\"query\":\"b\"}"),
                    ]),
                new ChatMessage(ChatRole.Tool, "A", ToolCallId: "toolu_1"),
                new ChatMessage(ChatRole.Tool, "B", ToolCallId: "toolu_2"),
            ],
            tools);

        var body = JsonNode.Parse(requestBody!)!;
        var messages = body["messages"]!.AsArray();
        var assistantContent = messages[0]!["content"]!.AsArray();
        Assert.AreEqual("text", assistantContent[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("tool_use", assistantContent[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("toolu_1", assistantContent[1]!["id"]!.GetValue<string>());
        Assert.AreEqual("a", assistantContent[1]!["input"]!["query"]!.GetValue<string>());
        var results = messages[1]!["content"]!.AsArray();
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("tool_result", results[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("toolu_2", results[1]!["tool_use_id"]!.GetValue<string>());
        var definition = body["tools"]![0]!;
        Assert.AreEqual("lookup", definition["name"]!.GetValue<string>());
        Assert.AreEqual("object", definition["input_schema"]!["type"]!.GetValue<string>());
        Assert.IsNull(definition["function"]);
    }

    [TestMethod]
    public async Task ChatAsync_Continuation_ReplaysSignedThinkingAndToolUseBlocks()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponse();
        });
        var continuation = new LlmContinuationState(
            "anthropic",
            [
                """{"type":"thinking","thinking":"plan","signature":"signed"}""",
                """{"type":"tool_use","id":"toolu_1","name":"lookup","input":{"q":1}}""",
            ]);

        await gateway.ChatAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    "projection must not duplicate blocks",
                    ToolCalls: [new ToolCall("toolu_1", "lookup", "{\"q\":1}")],
                    ContinuationState: continuation),
                new ChatMessage(ChatRole.Tool, "result", ToolCallId: "toolu_1"),
            ],
            []);

        var content = JsonNode.Parse(requestBody!)!["messages"]![0]!["content"]!.AsArray();
        Assert.AreEqual(2, content.Count);
        Assert.AreEqual("signed", content[0]!["signature"]!.GetValue<string>());
        Assert.AreEqual("tool_use", content[1]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ParsesTextThinkingToolsUsageAndContinuation()
    {
        const string responseJson = """
            {
              "id":"msg_1",
              "type":"message",
              "role":"assistant",
              "content":[
                {"type":"thinking","thinking":"plan","signature":"signed"},
                {"type":"text","text":"answer"},
                {"type":"tool_use","id":"toolu_1","name":"lookup","input":{"q":1}}
              ],
              "stop_reason":"tool_use",
              "usage":{"input_tokens":60,"cache_read_input_tokens":40,"cache_creation_input_tokens":10,"output_tokens":25}
            }
            """;
        var gateway = CreateGateway(_ => JsonResponse(responseJson));

        var response = await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []);

        Assert.AreEqual("answer", response.Content);
        Assert.AreEqual("plan", response.ReasoningContent);
        var call = response.ToolCalls!.Single();
        Assert.AreEqual("toolu_1", call.Id);
        Assert.AreEqual("lookup", call.Name);
        Assert.AreEqual("{\"q\":1}", call.ArgumentsJson);
        Assert.AreEqual(110, response.Usage!.PromptTokens);
        Assert.AreEqual(25, response.Usage.CompletionTokens);
        Assert.AreEqual(135, response.Usage.TotalTokens);
        Assert.AreEqual(40, response.Usage.PromptCacheHitTokens);
        Assert.AreEqual(70, response.Usage.PromptCacheMissTokens);
        Assert.AreEqual("anthropic", response.ContinuationState!.Protocol);
        Assert.AreEqual(3, response.ContinuationState.OutputItemsJson.Count);
    }

    [TestMethod]
    public async Task ChatStreamAsync_ToolEvents_MapDenseIndexAndPreserveContinuation()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","content":[],"usage":{"input_tokens":10,"cache_read_input_tokens":5}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"lookup","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"1}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":2}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var gateway = CreateGateway(_ => SseResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.AreEqual("plan", deltas[0].ReasoningDelta);
        Assert.AreEqual(0, deltas[1].ToolCallIndex);
        Assert.AreEqual("toolu_1", deltas[1].ToolCallId);
        Assert.AreEqual("lookup", deltas[1].ToolCallNameDelta);
        Assert.AreEqual("{\"q\":", deltas[2].ToolCallArgsDelta);
        Assert.AreEqual("1}", deltas[3].ToolCallArgsDelta);
        var terminal = deltas[4];
        Assert.AreEqual("tool_calls", terminal.FinishReason);
        Assert.AreEqual(19, terminal.Usage!.TotalTokens);
        Assert.AreEqual(2, terminal.ContinuationState!.OutputItemsJson.Count);
        StringAssert.Contains(terminal.ContinuationState.OutputItemsJson[0], "signed");
        StringAssert.Contains(terminal.ContinuationState.OutputItemsJson[1], "\"q\":1");
    }

    [TestMethod]
    public async Task ChatAsync_MultimodalInput_UsesAnthropicBase64ImageBlock()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new FixedVisualResolver();

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-1"])],
            []);

        var content = JsonNode.Parse(requestBody!)!["messages"]![0]!["content"]!.AsArray();
        Assert.AreEqual("text", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("image", content[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("base64", content[1]!["source"]!["type"]!.GetValue<string>());
        Assert.AreEqual("image/png", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.AreEqual("iVBORw0KGgo=", content[1]!["source"]!["data"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("https://provider.example/v1", "/v1/messages")]
    [DataRow("https://provider.example/v1/messages", "/v1/messages")]
    [DataRow("https://provider.example/v1/chat/completions", "/v1/messages")]
    [DataRow("https://provider.example/v1/responses", "/v1/messages")]
    public async Task EndpointNormalization_PostsToMessages(string endpoint, string expectedPath)
    {
        Uri? requestUri = null;
        var gateway = CreateGateway(
            request =>
            {
                requestUri = request.RequestUri;
                return OkResponse();
            },
            new LlmOptions(endpoint, "key", "qwen3.8-max", MaxTokens: 1024));

        await gateway.ChatAsync([new ChatMessage(ChatRole.User, "hello")], []);

        Assert.AreEqual(expectedPath, requestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task ChatAsync_HttpError_PreservesStatusCode()
    {
        var gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"type":"error","error":{"message":"bad key"}}"""),
        });

        var exception = await ThrowsHttpRequestExceptionAsync(
            () => gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []));

        Assert.AreEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    private static async Task<List<StreamDelta>> ReadStreamAsync(AnthropicMessagesLlmGateway gateway)
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

    private static AnthropicMessagesLlmGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        LlmOptions? options = null)
        => new(
            new HttpClient(new StubHttpMessageHandler(send)),
            options ?? new LlmOptions(
                "https://provider.example/v1",
                "test-key",
                "qwen3.8-max",
                MaxTokens: 131072));

    private static string ReadBody(HttpRequestMessage request)
        => request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage OkResponse()
        => JsonResponse(
            """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""");

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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed record HttpRequestHeadersSnapshot(
        string ApiKey,
        string Version,
        string? AuthorizationScheme);
}
