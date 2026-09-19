using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class VisionPreprocessingWireTests
{
    [TestMethod]
    public async Task UserAndToolImagesShareTheFifteenImageDimensionThreshold()
    {
        var handler = new Handler();
        var resolver = new PreparingResolver();
        var gateway = Create("responses", handler, resolver);
        await gateway.ChatAsync([
            new ChatMessage(ChatRole.User, "inspect", VisualArtifactIds: Enumerable.Range(0, 7).Select(i => $"user-{i}").ToArray()),
            new ChatMessage(ChatRole.Assistant, "reading", ToolCalls: [new ToolCall("call-1", "image_reader", "{}")]),
            new ChatMessage(ChatRole.Tool, "images", ToolCallId: "call-1", VisualArtifactIds: Enumerable.Range(0, 8).Select(i => $"tool-{i}").ToArray())
        ], []);
        Assert.AreEqual(15, resolver.Limits.Count);
        Assert.IsTrue(resolver.Limits.All(l => l.MaxEdge == 4096));
        Assert.AreEqual(15, CountImageNodes(JsonNode.Parse(handler.Body!)));
        StringAssert.Contains(handler.Body!, "function_call_output");
    }

    [TestMethod]
    [DataRow("responses")]
    [DataRow("openai")]
    [DataRow("anthropic")]
    public async Task FifteenImagesPrepareAt4096_And601FailsBeforeResolutionOrHttp(string protocol)
    {
        var handler = new Handler();
        var resolver = new PreparingResolver();
        var gateway = Create(protocol, handler, resolver);
        await gateway.ChatAsync(Images(15), []);
        Assert.AreEqual(15, resolver.Limits.Count);
        Assert.IsTrue(resolver.Limits.All(l => l.MaxEdge == 4096));
        Assert.AreEqual(15, CountImageNodes(JsonNode.Parse(handler.Body!)));
        resolver.Limits.Clear();
        await gateway.ChatAsync(Images(14), []);
        Assert.IsTrue(resolver.Limits.All(l => l.MaxEdge == 8192));
        await gateway.ChatAsync(Images(600), []);
        Assert.AreEqual(600, CountImageNodes(JsonNode.Parse(handler.Body!)));
        resolver.Limits.Clear();
        var calls = handler.Calls;
        var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => gateway.ChatAsync(Images(601), []));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, error.Code);
        Assert.AreEqual(calls, handler.Calls);
        Assert.IsEmpty(resolver.Limits);
        StringAssert.Contains(error.UserMessage, "601");
    }

    [TestMethod]
    [DataRow("responses")]
    [DataRow("openai")]
    [DataRow("anthropic")]
    public async Task BodyBudgetIncludesTextAndRecompressesBeforeSending(string protocol)
    {
        var handler = new Handler();
        var resolver = new PreparingResolver { FillByteBudget = true };
        var gateway = Create(protocol, handler, resolver);
        await gateway.ChatAsync([new ChatMessage(ChatRole.User, new string('a', 24 * 1024 * 1024), VisualArtifactIds: ["image"])], []);
        Assert.IsTrue(resolver.Limits.Count >= 2);
        Assert.IsTrue(resolver.Limits.Last().MaxBytes < resolver.Limits.First().MaxBytes);
        Assert.AreEqual(1, handler.Calls);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(handler.Body!) <= 48L * 1024 * 1024);
        Assert.AreEqual(1, CountImageNodes(JsonNode.Parse(handler.Body!)));
    }

    [TestMethod]
    public async Task TextEnvelopeAloneOver48MiBFailsBeforeHttp()
    {
        var handler = new Handler();
        var gateway = Create("responses", handler, new PreparingResolver());
        var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, new string('a', 48 * 1024 * 1024))], []));
        Assert.AreEqual(0, handler.Calls);
        StringAssert.Contains(error.UserMessage, "48 MiB");
    }

    private static ChatMessage[] Images(int count) => Enumerable.Range(0, count)
        .Select(i => new ChatMessage(ChatRole.User, "image", VisualArtifactIds: [$"image-{i}"])).ToArray();

    private static ILlmGateway Create(string protocol, Handler handler, PreparingResolver resolver)
    {
        var options = new LlmOptions("https://api.deepseek.com/v1", "test", "deepseek-flash");
        var client = new HttpClient(handler);
        return protocol switch
        {
            "openai" => new OpenAiLlmGateway(client, options) { WorkspaceId = "default", VisualArtifactResolver = resolver },
            "anthropic" => new AnthropicMessagesLlmGateway(client, options) { WorkspaceId = "default", VisualArtifactResolver = resolver },
            _ => new ResponsesLlmGateway(client, options) { WorkspaceId = "default", VisualArtifactResolver = resolver },
        };
    }

    private static int CountImageNodes(JsonNode? node) => node switch
    {
        JsonArray array => array.Sum(CountImageNodes),
        JsonObject obj => ((string?)obj["type"] is "input_image" or "image_url" or "image" ? 1 : 0) + obj.Sum(p => CountImageNodes(p.Value)),
        _ => 0,
    };

    private sealed class PreparingResolver : IVisualArtifactPreprocessor
    {
        public List<VisualArtifactPreparationOptions> Limits { get; } = [];
        public bool FillByteBudget { get; init; }
        public Task<VisualArtifactResolveResult?> ResolveAsync(string workspaceId, string artifactId, CancellationToken ct = default, string detail = "original")
            => throw new AssertFailedException("Request preparation must be used.");
        public Task<VisualArtifactResolveResult?> PrepareForRequestAsync(string workspaceId, string artifactId,
            VisualArtifactPreparationOptions options, CancellationToken ct = default, string detail = "original")
        {
            Limits.Add(options);
            var payload = FillByteBudget ? new string('A', (int)(options.MaxBytes / 3 * 4)) : "AAAA";
            return Task.FromResult<VisualArtifactResolveResult?>(new(artifactId, "data:image/png;base64," + payload, "image/png", options.MaxEdge, 1));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"r","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""", Encoding.UTF8, "application/json") };
        }
    }
}
