using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PuddingCoreTests;

/// <summary>
/// V5 切片四 T3（验收项③）：ResponsesLlmGateway wire 级视觉合同测试。
/// 对「首轮用户 input_image」与「工具 function_call_output 图片」两条来源路径分别断言：
/// ① 图片真实进入最终序列化请求体（stub HttpMessageHandler 捕获实际 POST body）；
/// ② 受网关真实入口注入的 Run Snapshot 视觉策略（<see cref="ResponsesLlmGateway.VisionPolicy"/>，
///    由 DirectLlmClient 单源写入）约束；
/// ③ 跨消息累计越界抛 <see cref="VisionPipelineException"/>（RequestLimitExceeded），
///    且 HTTP 请求未被发起（fail closed、不静默丢图）。
/// </summary>
[TestClass]
public sealed class ResponsesLlmGatewayVisualWireTests
{
    private const string PngDataUri = "data:image/png;base64,iVBORw0KGgo=";

    private static string Id(int index) => "vision-" + index.ToString("d32");

    private static IReadOnlyList<LlmImagePart> Parts(int startIndex, int count)
        => Enumerable.Range(startIndex, count).Select(i => new LlmImagePart(Id(i))).ToList();

    private sealed class FixedVisualResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default, string detail = PuddingCode.Models.VisionContentPartDetails.Original)
            => Task.FromResult<VisualArtifactResolveResult?>(new(artifactId, PngDataUri, "image/png"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"resp_1","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private static ResponsesLlmGateway CreateGateway(CapturingHandler handler)
    {
        var gateway = new ResponsesLlmGateway(
            new HttpClient(handler),
            new LlmOptions("https://provider.example/v1", "test-key", "deepseek-chat", MaxTokens: 64));
        gateway.WorkspaceId = "ws-vision";
        gateway.VisualArtifactResolver = new FixedVisualResolver();
        return gateway;
    }

    private static JsonObject ParseRequestBody(CapturingHandler handler)
        => JsonNode.Parse(handler.Bodies.Single())!.AsObject();

    private static JsonArray InputImageNodes(JsonArray content)
        => new(content.OfType<JsonObject>().Where(node => (string?)node["type"] == "input_image").Select(node => node.DeepClone()).ToArray());

    // ── V5 验收③·路径 A：首轮用户 input_image 真实进入最终请求体（wire 证据）──
    [TestMethod]
    public async Task ChatAsync_UserImages_AreSerializedIntoRequestBody()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "describe", ContentParts: Parts(1, 2))],
            []);

        Assert.AreEqual(1, handler.CallCount);
        var body = ParseRequestBody(handler);
        var firstMessage = body["input"]![0]!.AsObject();
        Assert.AreEqual("user", firstMessage["role"]!.GetValue<string>());
        var content = firstMessage["content"]!.AsArray();
        Assert.AreEqual("input_text", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("describe", content[0]!["text"]!.GetValue<string>());
        var images = InputImageNodes(content);
        // 两张图片都必须进入请求体（不静默丢图）。
        Assert.AreEqual(2, images.Count);
        foreach (var image in images.Cast<JsonObject>())
        {
            Assert.AreEqual(PngDataUri, image["image_url"]!.GetValue<string>());
            // canonical detail=original 等价 Responses high。
            Assert.AreEqual("high", image["detail"]!.GetValue<string>());
        }
    }

    // ── V5 验收③·路径 B：工具 function_call_output 图片真实进入最终请求体（wire 证据）──
    [TestMethod]
    public async Task ChatAsync_ToolImages_AreSerializedIntoFunctionCallOutput()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        await gateway.ChatAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    "checking",
                    ToolCalls: [new ToolCall("call-1", "lookup", "{}")]),
                new ChatMessage(ChatRole.Tool, "result", ToolCallId: "call-1", ContentParts: Parts(1, 2)),
            ],
            []);

        Assert.AreEqual(1, handler.CallCount);
        var body = ParseRequestBody(handler);
        var toolOutput = body["input"]!.AsArray()
            .OfType<JsonObject>()
            .Single(node => (string?)node["type"] == "function_call_output");
        Assert.AreEqual("call-1", toolOutput["call_id"]!.GetValue<string>());
        var output = toolOutput["output"]!.AsArray();
        Assert.AreEqual("input_text", output[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("result", output[0]!["text"]!.GetValue<string>());
        var images = InputImageNodes(output);
        // 工具返回的两张图片都必须出现在 function_call_output.output（DeepSeek Responses 合同允许）。
        Assert.AreEqual(2, images.Count);
        foreach (var image in images.Cast<JsonObject>())
        {
            Assert.AreEqual(PngDataUri, image["image_url"]!.GetValue<string>());
        }
    }

    // ── V5 验收③·跨消息越界：user 与 tool 图片在同一请求账本聚合，3+3+3=9 > 8 →
    //    RequestLimitExceeded 且 HTTP 未被发起（fail closed，绝不静默丢图）──
    [TestMethod]
    public async Task ChatAsync_CrossMessageUserAndToolImages_OverLimit_RejectedBeforeHttp()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        var exception = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            gateway.ChatAsync(
                [
                    new ChatMessage(ChatRole.User, "see", ContentParts: Parts(1, 3)),
                    new ChatMessage(
                        ChatRole.Assistant,
                        "checking",
                        ToolCalls:
                        [
                            new ToolCall("call-1", "lookup", "{}"),
                            new ToolCall("call-2", "lookup", "{}"),
                        ]),
                    new ChatMessage(ChatRole.Tool, "shot-1", ToolCallId: "call-1", ContentParts: Parts(11, 3)),
                    new ChatMessage(ChatRole.Tool, "shot-2", ToolCallId: "call-2", ContentParts: Parts(21, 3)),
                ],
                []));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, exception.Code);
        // 逐份计费：前 8 份合法通过，第 9 份在累计维度被拦截。
        StringAssert.Contains(exception.Message, "image count");
        StringAssert.Contains(exception.Message, "cumulative 8 + incoming 1");
        StringAssert.Contains(exception.Message, "policy limit 8");
        // 异常消息定位到来源（工具 function_call_output）与消息序号。
        StringAssert.Contains(exception.Message, "tool function_call_output @message#");
        Assert.AreEqual(0, handler.CallCount, "越界必须发生在 HTTP 发起之前（fail closed）。");
    }

    // ── V5 验收②：视觉策略经网关真实入口（DirectLlmClient 注入的 VisionPolicy）生效——
    //    合同值 2 覆盖默认 8，第二条 user 消息在账本聚合后被拦截 ──
    [TestMethod]
    public async Task ChatAsync_VisionPolicyFromGatewayEntry_ConstrainsRequestLedger()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);
        gateway.VisionPolicy = new VisionRequestPolicy { MaxImagesPerRequest = 2 };

        var exception = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            gateway.ChatAsync(
                [
                    new ChatMessage(ChatRole.User, "a", ContentParts: Parts(1, 2)),
                    new ChatMessage(ChatRole.User, "b", ContentParts: Parts(11, 2)),
                ],
                []));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, exception.Code);
        // 第一条消息 2 份用尽合同额度；第二条消息首份即越界——若策略未被网关入口采纳（默认 8）则不会拦截。
        StringAssert.Contains(exception.Message, "cumulative 2 + incoming 1");
        StringAssert.Contains(exception.Message, "policy limit 2");
        StringAssert.Contains(exception.Message, "user input_image @message#");
        Assert.AreEqual(0, handler.CallCount);
    }
}
