using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PuddingCoreTests.Vision;

[TestClass]
public sealed class DashScopeVisualReasoningProviderTests
{
    [TestMethod]
    public async Task StreamAsync_Maps_OpenAiCompatible_Sse_To_Normalized_Vision_Events()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n",
                "data: {\"id\":\"chatcmpl-vision-1\",\"choices\":[{\"delta\":{\"reasoning_content\":\"先看图。\"},\"index\":0,\"finish_reason\":null}]}",
                "",
                "data: {\"id\":\"chatcmpl-vision-1\",\"choices\":[{\"delta\":{\"content\":\"答案是B\"},\"index\":0,\"finish_reason\":null}]}",
                "",
                "data: {\"id\":\"chatcmpl-vision-1\",\"choices\":[{\"delta\":{\"content\":\"\"},\"index\":0,\"finish_reason\":\"stop\"}]}",
                "",
                "data: [DONE]",
                ""), Encoding.UTF8, "text/event-stream"),
        });
        var provider = new DashScopeVisualReasoningProvider(
            new HttpClient(handler),
            new DashScopeVisualReasoningOptions("https://dashscope.aliyuncs.com/compatible-mode/v1", "sk-test"));

        var events = new List<VisualReasoningStreamEvent>();
        await foreach (var item in provider.StreamAsync(CreateRequest()))
        {
            events.Add(item);
        }

        Assert.AreEqual(VisualReasoningStreamEventTypes.SessionStarted, events[0].Type);
        Assert.AreEqual(VisualReasoningStreamEventTypes.ReasoningDelta, events[1].Type);
        Assert.AreEqual("先看图。", events[1].ReasoningDelta);
        Assert.AreEqual(VisualReasoningStreamEventTypes.AnswerDelta, events[2].Type);
        Assert.AreEqual("答案是B", events[2].AnswerDelta);
        Assert.AreEqual(VisualReasoningStreamEventTypes.Completed, events[^1].Type);
        Assert.IsTrue(events.All(item => item.ProviderRawPayload is null));
    }

    [TestMethod]
    public async Task StreamAsync_Builds_DashScope_OpenAiCompatible_Request_With_Thinking_Controls()
    {
        string? requestJson = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        var provider = new DashScopeVisualReasoningProvider(
            new HttpClient(handler),
            new DashScopeVisualReasoningOptions("https://dashscope.aliyuncs.com/compatible-mode/v1", "sk-test"));

        await foreach (var _ in provider.StreamAsync(CreateRequest()))
        {
        }

        Assert.IsNotNull(requestJson);
        var body = JsonNode.Parse(requestJson)!;
        Assert.AreEqual("qwen3-vl-plus", body["model"]!.GetValue<string>());
        Assert.IsTrue(body["stream"]!.GetValue<bool>());
        Assert.IsTrue(body["enable_thinking"]!.GetValue<bool>());
        Assert.AreEqual(81920, body["thinking_budget"]!.GetValue<int>());

        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.AreEqual("image_url", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("https://example.test/frame.jpg", content[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.AreEqual("text", content[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("相对于当前位置，哪个对象最远？", content[1]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AnalyzeAsync_Aggregates_Streamed_Answer_Reasoning_And_Usage()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n",
                "data: {\"id\":\"chatcmpl-vision-2\",\"choices\":[{\"delta\":{\"reasoning_content\":\"看空间关系。\"},\"index\":0,\"finish_reason\":null}]}",
                "",
                "data: {\"id\":\"chatcmpl-vision-2\",\"choices\":[{\"delta\":{\"content\":\"B\"},\"index\":0,\"finish_reason\":null}]}",
                "",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":544,\"completion_tokens\":590,\"total_tokens\":1134,\"prompt_tokens_details\":{\"image_tokens\":520}}}",
                "",
                "data: [DONE]",
                ""), Encoding.UTF8, "text/event-stream"),
        });
        var provider = new DashScopeVisualReasoningProvider(
            new HttpClient(handler),
            new DashScopeVisualReasoningOptions("https://dashscope.aliyuncs.com/compatible-mode/v1", "sk-test"));

        var result = await provider.AnalyzeAsync(CreateRequest());

        Assert.AreEqual("B", result.Answer);
        Assert.AreEqual("看空间关系。", result.ReasoningSummary);
        Assert.AreEqual("chatcmpl-vision-2", result.RequestId);
        Assert.AreEqual(544, result.InputTokens);
        Assert.AreEqual(590, result.OutputTokens);
        Assert.AreEqual(520, result.ImageTokens);
        Assert.AreEqual("vision", result.Metadata["inputMode"]);
        Assert.AreEqual(VisualReasoningProviders.DashScope, result.Metadata["visionProvider"]);
    }

    private static VisualReasoningRequest CreateRequest() => new()
    {
        WorkspaceId = "default",
        RoomId = "room-default",
        ParticipantId = "user-owner",
        SessionId = "vision-session-test",
        Provider = VisualReasoningProviders.DashScope,
        Model = "qwen3-vl-plus",
        Transport = VisualReasoningTransports.OpenAiCompatibleSse,
        OutputMode = VisualReasoningOutputModes.Streaming,
        ThinkingMode = VisualReasoningThinkingModes.Toggleable,
        EnableThinking = true,
        ThinkingBudgetTokens = 81920,
        Prompt = "相对于当前位置，哪个对象最远？",
        Inputs =
        [
            new VisualInputArtifact
            {
                ArtifactId = "frame-1",
                Kind = VisualInputKinds.ImageUrl,
                MimeType = VisualMimeTypes.Jpeg,
                Uri = "https://example.test/frame.jpg",
                Width = 1280,
                Height = 720,
                CapturedAt = 1234,
            },
        ],
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
