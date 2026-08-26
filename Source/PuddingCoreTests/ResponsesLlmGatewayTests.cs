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
    public async Task ChatAsync_ImagePartsWithoutResolver_TextRouteOmitsImages()
    {
        // ADR-077 §5.5：无视觉解析通道 = 文本路由；图片部件不进入请求（模型以正文 artifact:// 占位为准）。
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = null;

        await gateway.ChatAsync(
            [new ChatMessage(
                ChatRole.User,
                "artifact://vision-1 placeholder",
                ContentParts: [new PuddingCode.Models.LlmImagePart("vision-1")])],
            []);

        var content = JsonNode.Parse(requestBody!)!["input"]![0]!["content"]!;
        Assert.AreEqual("artifact://vision-1 placeholder", content.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ResolverFailure_FailsClosedInsteadOfSilentText()
    {
        var gateway = CreateGateway(_ => OkResponsesResponse());
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new ThrowingVisualResolver();

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => gateway.ChatAsync(
            [new ChatMessage(
                ChatRole.User,
                "describe",
                VisualArtifactIds: ["vision-missing"])],
            []));

        Assert.AreEqual(VisionErrorCodes.ArtifactMissing, ex.Code);
    }

    [TestMethod]
    public async Task ChatAsync_CanonicalImagePart_MapsDetailOriginalToHigh()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new FixedVisualResolver();

        await gateway.ChatAsync(
            [new ChatMessage(
                ChatRole.User,
                "describe",
                ContentParts:
                [
                    new PuddingCode.Models.LlmTextPart("describe"),
                    new PuddingCode.Models.LlmImagePart("vision-1"),
                    new PuddingCode.Models.LlmImagePart("vision-2", PuddingCode.Models.VisionContentPartDetails.Low),
                ])],
            []);

        var content = JsonNode.Parse(requestBody!)!["input"]![0]!["content"]!.AsArray();
        Assert.AreEqual(3, content.Count);
        Assert.AreEqual("high", content[1]!["detail"]!.GetValue<string>());
        Assert.AreEqual("low", content[2]!["detail"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_ToolResultWithImageParts_SerializesTypedOutputArray()
    {
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new FixedVisualResolver();

        await gateway.ChatAsync(
            [
                new ChatMessage(ChatRole.User, "read this image"),
                new ChatMessage(ChatRole.Assistant, null, ToolCalls: [new ToolCall("call-1", "image_reader", "{}")]),
                new ChatMessage(
                    ChatRole.Tool,
                    "image_reader loaded one image",
                    ToolCallId: "call-1",
                    ContentParts: [new PuddingCode.Models.LlmImagePart("vision-1")]),
            ],
            []);

        var input = JsonNode.Parse(requestBody!)!["input"]!.AsArray();
        var toolOutput = input.FirstOrDefault(node => node?["type"]?.GetValue<string>() == "function_call_output");
        Assert.IsNotNull(toolOutput, "function_call_output item missing");
        Assert.AreEqual("call-1", toolOutput!["call_id"]!.GetValue<string>());
        var output = toolOutput["output"]!.AsArray();
        Assert.AreEqual("input_text", output[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("image_reader loaded one image", output[0]!["text"]!.GetValue<string>());
                Assert.AreEqual("input_image", output[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", output[1]!["image_url"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatAsync_FileModeImage_UsesFileIdWithoutImageUrl()
    {
        // ADR-077 V3-S2a：大图（>2MB）经 Files API 上传后以 file_id 引用，不输出 image_url/detail。
        string? requestBody = null;
        var gateway = CreateGateway(request =>
        {
            requestBody = ReadBody(request);
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new OversizeVisualResolver();
        gateway.DeepSeekFilesUploader = new RecordingFilesUploader();

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-big"])],
            []);

                var content = JsonNode.Parse(requestBody!)!["input"]![0]!["content"]!.AsArray();
        var image = content.First(node => node?["type"]?.GetValue<string>() == "input_image");
        Assert.AreEqual("file-uploaded-001", image!["file_id"]!.GetValue<string>());
        Assert.IsNull(image["image_url"], "file 模式不得输出 image_url");
        Assert.IsNull(image["detail"], "file 模式忽略 detail（ADR-077 §6.1）");
    }

    [TestMethod]
    public async Task ChatAsync_FileModeImage_ProviderFileExpired_RebuildsOnce()
    {
        // ADR-077 V3-S2b-2「调用时过期」：store 命中复用 file_id → Gateway 调用发现 file 已失效
        // （provider 报 file expired）→ 恰好一次 MarkExpired + 重新上传落库 + 重发，第二次成功。
        var store = new GatewayFileRefStore();
        store.Seed(
            ComputeHash("data:image/png;base64," + new string('A', 3_000_000)),
            new ProviderFileRefRecord(
                ProviderId: "deepseek",
                CredentialEpoch: "default",
                ArtifactId: "vision-big",
                ArtifactSha256: ComputeHash(
                    "data:image/png;base64," + new string('A', 3_000_000)),
                RemoteFileId: "file-cached-001",
                Bytes: 2_250_000,
                MimeType: "image/png",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                LastUsedAt: null,
                Status: ProviderFileRefStatus.Ready,
                CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var capturedBodies = new List<string>();
        var sendCount = 0;
        var gateway = CreateGateway(_ =>
        {
            sendCount++;
            if (sendCount == 1)
            {
                // 首次调用：provider 报 file 已失效（HTTP 400 + file expired 信号）。
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"File file-cached-001 has expired","type":"invalid_request_error"}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            capturedBodies.Add(ReadBody(_));
            return OkResponsesResponse();
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new OversizeVisualResolver();
        gateway.DeepSeekFilesUploader = new RecordingFilesUploader();
        gateway.FileRefStore = store;
        gateway.ProviderId = "deepseek";
        gateway.CredentialEpoch = "default";

        var response = await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-big"])],
            []);

        Assert.AreEqual("ok", response.Content, "重建后第二次调用应成功");
        Assert.AreEqual(2, sendCount, "首次失败 + 重建后重发 = 恰好两次 provider 调用");
        Assert.AreEqual(1, store.MarkExpiredCount, "过期引用恰好 MarkExpired 一次");
        Assert.AreEqual(1, store.SaveCount, "重建后落库 ready 一次");
        var rebuiltBody = capturedBodies.Single();
        var content = JsonNode.Parse(rebuiltBody)!["input"]![0]!["content"]!.AsArray();
        var image = content.First(node => node?["type"]?.GetValue<string>() == "input_image");
        Assert.AreEqual("file-uploaded-001", image!["file_id"]!.GetValue<string>(),
            "重建后的请求体应使用重新上传的新 file_id");
        var saved = store.Saved.Single();
        Assert.AreEqual(ProviderFileRefStatus.Ready, saved.Status);
        Assert.AreEqual("file-uploaded-001", saved.RemoteFileId);
    }

    [TestMethod]
    public async Task ChatAsync_FileModeImage_ProviderFileExpired_RebuildFails_ThrowsProviderFileExpired()
    {
        // ADR-077 V3-S2b-2：重建后 provider 仍报错 → 抛 ProviderFileExpired（不盲目重试）。
        var store = new GatewayFileRefStore();
        store.Seed(
            ComputeHash("data:image/png;base64," + new string('A', 3_000_000)),
            new ProviderFileRefRecord(
                ProviderId: "deepseek",
                CredentialEpoch: "default",
                ArtifactId: "vision-big",
                ArtifactSha256: ComputeHash(
                    "data:image/png;base64," + new string('A', 3_000_000)),
                RemoteFileId: "file-cached-001",
                Bytes: 2_250_000,
                MimeType: "image/png",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                LastUsedAt: null,
                Status: ProviderFileRefStatus.Ready,
                CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var sendCount = 0;
        var gateway = CreateGateway(_ =>
        {
            sendCount++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":{"message":"File still expired","type":"invalid_request_error"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new OversizeVisualResolver();
        gateway.DeepSeekFilesUploader = new RecordingFilesUploader();
        gateway.FileRefStore = store;
        gateway.ProviderId = "deepseek";
        gateway.CredentialEpoch = "default";

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            gateway.ChatAsync(
                [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-big"])],
                []));
        Assert.AreEqual(VisionErrorCodes.ProviderFileExpired, ex.Code);
        Assert.AreEqual(2, sendCount, "重建后仍失败不得再次重试");
        Assert.AreEqual(1, store.MarkExpiredCount);
    }

    [TestMethod]
    public async Task ChatAsync_FileModeImage_NonFileError_DoesNotRebuild()
    {
        // ADR-077 V3-S2b-2：非 file 失效错误不得触发重建（保持既有失败语义）。
        var store = new GatewayFileRefStore();
        store.Seed(
            ComputeHash("data:image/png;base64," + new string('A', 3_000_000)),
            new ProviderFileRefRecord(
                ProviderId: "deepseek",
                CredentialEpoch: "default",
                ArtifactId: "vision-big",
                ArtifactSha256: ComputeHash(
                    "data:image/png;base64," + new string('A', 3_000_000)),
                RemoteFileId: "file-cached-001",
                Bytes: 2_250_000,
                MimeType: "image/png",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                LastUsedAt: null,
                Status: ProviderFileRefStatus.Ready,
                CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var sendCount = 0;
        var gateway = CreateGateway(_ =>
        {
            sendCount++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":{"message":"rate limit exceeded","type":"rate_limit"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new OversizeVisualResolver();
        gateway.DeepSeekFilesUploader = new RecordingFilesUploader();
        gateway.FileRefStore = store;
        gateway.ProviderId = "deepseek";
        gateway.CredentialEpoch = "default";

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            gateway.ChatAsync(
                [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-big"])],
                []));
        Assert.AreEqual(1, sendCount, "非 file 失效错误不得重试");
        Assert.AreEqual(0, store.MarkExpiredCount);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task ChatStreamAsync_FileModeImage_ProviderFileExpired_RebuildsOnce()
    {
        // ADR-077 V3-S2b-2：流式同样覆盖「调用时过期」重建一次。
        var store = new GatewayFileRefStore();
        store.Seed(
            ComputeHash("data:image/png;base64," + new string('A', 3_000_000)),
            new ProviderFileRefRecord(
                ProviderId: "deepseek",
                CredentialEpoch: "default",
                ArtifactId: "vision-big",
                ArtifactSha256: ComputeHash(
                    "data:image/png;base64," + new string('A', 3_000_000)),
                RemoteFileId: "file-cached-001",
                Bytes: 2_250_000,
                MimeType: "image/png",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                LastUsedAt: null,
                Status: ProviderFileRefStatus.Ready,
                CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var sendCount = 0;
        var gateway = CreateGateway(_ =>
        {
            sendCount++;
            return sendCount == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"File file-cached-001 has expired","type":"invalid_request_error"}}""",
                        Encoding.UTF8,
                        "application/json"),
                }
                : SseResponse(
                    """
data:{"type":"response.output_text.delta","delta":"ok"}

data:{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4}}}

data:[DONE]

""");
        });
        gateway.WorkspaceId = "workspace-1";
        gateway.VisualArtifactResolver = new OversizeVisualResolver();
        gateway.DeepSeekFilesUploader = new RecordingFilesUploader();
        gateway.FileRefStore = store;
        gateway.ProviderId = "deepseek";
        gateway.CredentialEpoch = "default";

        var deltas = new List<StreamDelta>();
        await foreach (var delta in gateway.ChatStreamAsync(
                           [new ChatMessage(ChatRole.User, "describe", VisualArtifactIds: ["vision-big"])],
                           []))
        {
            deltas.Add(delta);
        }

        Assert.IsTrue(deltas.Count > 0, "重建后流式第二次调用应产生输出");
        Assert.AreEqual(2, sendCount, "首次失败 + 重建后重发 = 恰好两次 provider 调用");
        Assert.AreEqual(1, store.MarkExpiredCount);
        Assert.AreEqual(1, store.SaveCount);
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
    public async Task ChatAsync_FailedTerminalStatus_Throws()
    {
        const string detail = "provider exploded";
        var json = $$"""{"status":"failed","error":{"message":"{{detail}}"},"output":[]}""";
        var gateway = CreateGateway(_ => JsonResponse(json));

        var exception = await ThrowsHttpRequestExceptionAsync(
            () => gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []));

        StringAssert.Contains(exception.Message, detail);
    }

    [TestMethod]
    public async Task ChatAsync_IncompleteResponse_ReturnsPartialOutputUsageAndContinuation()
    {
        const string responseJson = """
            {
              "status":"incomplete",
              "incomplete_details":{"reason":"max_output_tokens"},
              "output":[
                {"type":"reasoning","id":"rs_1","content":[{"type":"reasoning_text","text":"working"}]},
                {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"partial answer"}]},
                {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"q\":"}
              ],
              "usage":{"input_tokens":100,"output_tokens":4096,"total_tokens":4196}
            }
            """;
        var gateway = CreateGateway(_ => JsonResponse(responseJson));

        var response = await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect")], []);

        Assert.AreEqual("partial answer", response.Content);
        Assert.AreEqual("working", response.ReasoningContent);
        Assert.IsNull(response.ToolCalls, "A truncated function call must not be executed.");
        Assert.AreEqual(4196, response.Usage!.TotalTokens);
        Assert.AreEqual(3, response.ContinuationState!.OutputItemsJson.Count);
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
    public async Task ChatStreamAsync_TerminalErrorEvents_Throw(string eventType)
    {
        var payload = eventType switch
        {
            "error" => """{"type":"error","error":{"message":"rate limited"}}""",
            _ => """{"type":"response.failed","response":{"status":"failed","error":{"message":"provider failed"}}}""",
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
    public async Task ChatStreamAsync_DeepSeekReasoningAndIncomplete_EmitAuditableTerminalData()
    {
        const string sse = """
            data:{"type":"response.reasoning_text.delta","delta":"working"}

            data:{"type":"response.output_text.delta","delta":"partial answer"}

            data:{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":""}}

            data:{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"{\"q\":"}

            data:{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"reasoning","id":"rs_1","content":[{"type":"reasoning_text","text":"working"}]},{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"partial answer"}]},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"q\":"}],"usage":{"input_tokens":100,"output_tokens":4096,"total_tokens":4196}}}

            """;
        var gateway = CreateGateway(_ => SseResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.AreEqual("working", deltas[0].ReasoningDelta);
        Assert.AreEqual("partial answer", deltas[1].ContentDelta);
        Assert.AreEqual("lookup", deltas[2].ToolCallNameDelta);
        Assert.AreEqual("{\"q\":", deltas[3].ToolCallArgsDelta);
        Assert.AreEqual("length", deltas[4].FinishReason);
        Assert.AreEqual(4196, deltas[4].Usage!.TotalTokens);
        Assert.AreEqual(3, deltas[4].ContinuationState!.OutputItemsJson.Count);
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

    private sealed class OversizeVisualResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<VisualArtifactResolveResult?>(new(
                artifactId,
                "data:image/png;base64," + new string('A', 3_000_000),
                "image/png"));
    }

    private static string ComputeHash(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        var payload = dataUri[(comma + 1)..];
        var bytes = Convert.FromBase64String(payload);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class RecordingFilesUploader : IDeepSeekFilesUploader
    {
        public Task<ProviderFileUploadResult> UploadAsync(
            byte[] imageBytes,
            string mimeType,
            long lifetimeSeconds,
            CancellationToken ct = default)
            => Task.FromResult(new ProviderFileUploadResult(
                "file-uploaded-001",
                mimeType,
                imageBytes.Length,
                lifetimeSeconds,
                DateTimeOffset.UnixEpoch));
    }

    private sealed class ThrowingVisualResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
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

    /// <summary>内存版 <see cref="IFileRefStore"/>（ADR-077 V3-S2b-2 Gateway 调用时过期重建测试用）。</summary>
    private sealed class GatewayFileRefStore : IFileRefStore
    {
        private readonly Dictionary<string, ProviderFileRefRecord> _records = new();

        public int MarkExpiredCount { get; private set; }
        public int SaveCount { get; private set; }
        public List<ProviderFileRefRecord> Saved { get; } = new();

        public void Seed(string artifactSha256, ProviderFileRefRecord record)
            => _records[artifactSha256] = record;

        public Task<ProviderFileRefRecord?> TryGetReadyRefAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            CancellationToken ct = default)
        {
            if (!_records.TryGetValue(artifactSha256, out var record)
                || record.ProviderId != providerId
                || record.CredentialEpoch != credentialEpoch
                || record.Status != ProviderFileRefStatus.Ready)
                return Task.FromResult<ProviderFileRefRecord?>(null);
            return Task.FromResult<ProviderFileRefRecord?>(record);
        }

        public Task<ProviderFileRefRecord> SaveAsync(ProviderFileRefRecord record, CancellationToken ct = default)
        {
            SaveCount++;
            Saved.Add(record);
            _records[record.ArtifactSha256] = record;
            return Task.FromResult(record);
        }

        public Task<ProviderFileRefRecord?> MarkExpiredAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
        {
            MarkExpiredCount++;
            if (!_records.TryGetValue(artifactSha256, out var record))
                return Task.FromResult<ProviderFileRefRecord?>(null);
            var expired = record with
            {
                Status = ProviderFileRefStatus.Expired,
                UpdatedAt = updatedAt,
            };
            _records[artifactSha256] = expired;
            return Task.FromResult<ProviderFileRefRecord?>(expired);
        }

        public Task<ProviderFileRefRecord?> UpdateExpiryAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset newExpiresAt,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => Task.FromResult<ProviderFileRefRecord?>(null);

        public Task<ProviderFileRefRecord?> MarkDeletePendingAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => Task.FromResult<ProviderFileRefRecord?>(null);

        public Task<IReadOnlyList<ProviderFileRefRecord>> ListExpiredAsync(
            DateTimeOffset before,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileRefRecord>>([]);
    }
}
