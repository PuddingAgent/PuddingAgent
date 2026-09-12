using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PuddingCoreTests;

/// <summary>
/// V5 切片四 T3（验收项③）：AnthropicMessagesLlmGateway wire 级视觉合同测试。
/// 协议差异：Anthropic 用户图片以 base64 image block 编码（与其他网关形态不同，按真实形态断言）；
/// 图片型工具结果不受支持——路径 B 的真实合同是 fail closed（ToolOutputNotSupported、HTTP 不发起）。
/// 路径 A 断言：① 真实进入最终请求体；② 受网关入口 VisionPolicy 约束；
/// ③ 跨消息累计越界抛 RequestLimitExceeded 且 HTTP 未被发起（fail closed、不静默丢图）。
/// </summary>
[TestClass]
public sealed class AnthropicMessagesLlmGatewayVisualWireTests
{
    private const string PngDataUri = "data:image/png;base64,iVBORw0KGgo=";
    private const string PngBase64Payload = "iVBORw0KGgo=";

    private static string Id(int index) => "vision-" + index.ToString("d32");

    private static IReadOnlyList<LlmImagePart> Parts(int startIndex, int count)
        => Enumerable.Range(startIndex, count).Select(i => new LlmImagePart(Id(i))).ToList();

    private sealed class FixedVisualResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
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
                    """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private static AnthropicMessagesLlmGateway CreateGateway(CapturingHandler handler)
    {
        var gateway = new AnthropicMessagesLlmGateway(
            new HttpClient(handler),
            new LlmOptions("https://provider.example/v1", "test-key", "qwen3.8-max", MaxTokens: 131072));
        gateway.WorkspaceId = "ws-vision";
        gateway.VisualArtifactResolver = new FixedVisualResolver();
        return gateway;
    }

    private static JsonObject ParseRequestBody(CapturingHandler handler)
        => JsonNode.Parse(handler.Bodies.Single())!.AsObject();

    private static JsonArray ImageBlockNodes(JsonArray content)
        => new(content.OfType<JsonObject>().Where(node => (string?)node["type"] == "image").Select(node => node.DeepClone()).ToArray());

    // ── V5 验收③·路径 A：首轮用户图片以 Anthropic base64 image block 真实进入最终请求体 ──
    [TestMethod]
    public async Task ChatAsync_UserImages_AreSerializedAsBase64ImageBlocks()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "describe", ContentParts: Parts(1, 2))],
            []);

        Assert.AreEqual(1, handler.CallCount);
        var body = ParseRequestBody(handler);
        var firstMessage = body["messages"]![0]!.AsObject();
        Assert.AreEqual("user", firstMessage["role"]!.GetValue<string>());
        var content = firstMessage["content"]!.AsArray();
        Assert.AreEqual("text", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("describe", content[0]!["text"]!.GetValue<string>());
        var images = ImageBlockNodes(content);
        // 两张图片都必须进入请求体（不静默丢图）；形态为 Anthropic base64 source block。
        Assert.AreEqual(2, images.Count);
        foreach (var image in images.Cast<JsonObject>())
        {
            Assert.AreEqual("base64", image["source"]!["type"]!.GetValue<string>());
            Assert.AreEqual("image/png", image["source"]!["media_type"]!.GetValue<string>());
            Assert.AreEqual(PngBase64Payload, image["source"]!["data"]!.GetValue<string>());
        }
    }

    // ── V5 验收③·路径 B（协议差异形态）：Anthropic Messages 不支持图片型工具结果——
    //    必须抛 ToolOutputNotSupported，图片绝不进入请求体，HTTP 不发起（fail closed）──
    [TestMethod]
    public async Task ChatAsync_ToolImages_NotSupportedByProtocol_FailClosedBeforeHttp()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        var exception = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            gateway.ChatAsync(
                [
                    new ChatMessage(
                        ChatRole.Assistant,
                        "checking",
                        ToolCalls: [new ToolCall("toolu_1", "lookup", "{}")]),
                    new ChatMessage(ChatRole.Tool, "result", ToolCallId: "toolu_1", ContentParts: Parts(1, 2)),
                ],
                []));

        Assert.AreEqual(VisionErrorCodes.ToolOutputNotSupported, exception.Code);
        StringAssert.Contains(exception.Message, "Anthropic Messages");
        Assert.AreEqual(0, handler.CallCount, "协议不支持的图片型工具结果必须在 HTTP 发起之前 fail closed。");
    }

    // ── V5 验收③·跨消息越界：三条 user 消息各 3 张，3+3+3=9 > 8 →
    //    RequestLimitExceeded（来源标注 anthropic + 消息序号），HTTP 未发起 ──
    [TestMethod]
    public async Task ChatAsync_CrossMessageUserImages_OverLimit_RejectedBeforeHttp()
    {
        var handler = new CapturingHandler();
        var gateway = CreateGateway(handler);

        var exception = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            gateway.ChatAsync(
                [
                    new ChatMessage(ChatRole.User, "a", ContentParts: Parts(1, 3)),
                    new ChatMessage(ChatRole.User, "b", ContentParts: Parts(11, 3)),
                    new ChatMessage(ChatRole.User, "c", ContentParts: Parts(21, 3)),
                ],
                []));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, exception.Code);
        StringAssert.Contains(exception.Message, "image count");
        StringAssert.Contains(exception.Message, "cumulative 8 + incoming 1");
        StringAssert.Contains(exception.Message, "policy limit 8");
        StringAssert.Contains(exception.Message, "user input_image @message#");
        StringAssert.Contains(exception.Message, "(anthropic)");
        Assert.AreEqual(0, handler.CallCount, "越界必须发生在 HTTP 发起之前（fail closed）。");
    }

    // ── V5 验收②：视觉策略经网关真实入口（DirectLlmClient 注入的 VisionPolicy）生效——
    //    合同值 2 覆盖默认 8，第二条 user 消息在请求级账本聚合后被拦截 ──
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
        StringAssert.Contains(exception.Message, "cumulative 2 + incoming 1");
        StringAssert.Contains(exception.Message, "policy limit 2");
        Assert.AreEqual(0, handler.CallCount);
    }
}
