using System.Net;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class OpenAiLlmGatewayCompatibilityTests
{
    [TestMethod]
    public async Task ChatStreamAsync_MissingIdsAndMultiCallChunk_EmitsEveryCallWithStableUniqueIds()
    {
        const string sse = """
            data: {"id":"chatcmpl-qwen","choices":[{"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"first","arguments":"{\"value\":"}},{"index":1,"type":"function","function":{"name":"second","arguments":"{\"value\":2}"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-qwen","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => StreamingResponse(sse));

        var deltas = await ReadStreamAsync(gateway);
        var toolDeltas = deltas.Where(delta => delta.ToolCallIndex is not null).ToList();

        Assert.AreEqual(3, toolDeltas.Count, "Every tool_calls entry in a provider chunk must be preserved.");
        var firstId = toolDeltas[0].ToolCallId;
        var secondId = toolDeltas[1].ToolCallId;
        Assert.IsFalse(string.IsNullOrWhiteSpace(firstId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(secondId));
        Assert.AreNotEqual(firstId, secondId);
        Assert.AreEqual(firstId, toolDeltas[2].ToolCallId, "Synthetic IDs must remain stable across chunks for one call index.");
        Assert.IsTrue(toolDeltas.All(delta => delta.ToolCallIdWasSynthesized));
        Assert.IsNotNull(toolDeltas[0].ProviderReadMs);
        Assert.IsNull(toolDeltas[1].ProviderReadMs, "One provider chunk must contribute transport metrics only once.");

        var completedCalls = toolDeltas
            .GroupBy(delta => delta.ToolCallIndex!.Value)
            .Select(group => group.Last())
            .ToList();
        var normalized = LlmMessageSequenceNormalizer.Normalize(
        [
            new ChatMessage(
                ChatRole.Assistant,
                null,
                ToolCalls: completedCalls
                    .Select(delta => new ToolCall(delta.ToolCallId!, delta.ToolCallNameDelta ?? "tool", "{}"))
                    .ToList()),
            .. completedCalls.Select(delta =>
                new ChatMessage(ChatRole.Tool, "ok", ToolCallId: delta.ToolCallId)),
        ]);
        Assert.IsFalse(normalized.Changed, "Repaired IDs must form a complete replayable OpenAI tool round.");
    }

    [TestMethod]
    public async Task ChatStreamAsync_LateProviderId_ReplacesTemporarySyntheticId()
    {
        const string sse = """
            data: {"id":"chatcmpl-late","choices":[{"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"inspect","arguments":"{"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-late","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_provider_1","function":{"arguments":"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => StreamingResponse(sse));

        var toolDeltas = (await ReadStreamAsync(gateway))
            .Where(delta => delta.ToolCallIndex is not null)
            .ToList();

        Assert.AreEqual(2, toolDeltas.Count);
        Assert.IsTrue(toolDeltas[0].ToolCallIdWasSynthesized);
        Assert.AreEqual("call_provider_1", toolDeltas[1].ToolCallId);
        Assert.IsFalse(toolDeltas[1].ToolCallIdWasSynthesized);
    }

    [TestMethod]
    public async Task ChatStreamAsync_DuplicateProviderIds_RepairsTheSecondCall()
    {
        const string sse = """
            data: {"id":"chatcmpl-duplicate","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_duplicate","type":"function","function":{"name":"first","arguments":"{}"}},{"index":1,"id":"call_duplicate","type":"function","function":{"name":"second","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => StreamingResponse(sse));

        var toolDeltas = (await ReadStreamAsync(gateway))
            .Where(delta => delta.ToolCallIndex is not null)
            .ToList();

        Assert.AreEqual("call_duplicate", toolDeltas[0].ToolCallId);
        Assert.IsFalse(toolDeltas[0].ToolCallIdWasSynthesized);
        Assert.IsFalse(string.IsNullOrWhiteSpace(toolDeltas[1].ToolCallId));
        Assert.AreNotEqual(toolDeltas[0].ToolCallId, toolDeltas[1].ToolCallId);
        Assert.IsTrue(toolDeltas[1].ToolCallIdWasSynthesized);
    }

    [TestMethod]
    public async Task ChatAsync_MissingAndDuplicateIds_ReturnsProtocolSafeToolCalls()
    {
        const string json = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"type":"function","function":{"name":"first","arguments":"{}"}},{"id":"call_same","type":"function","function":{"name":"second","arguments":"{}"}},{"id":"call_same","type":"function","function":{"name":"third","arguments":"{}"}}]}}]}
            """;
        var gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        var response = await gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "inspect")],
            Array.Empty<ITool>());

        Assert.IsNotNull(response.ToolCalls);
        Assert.AreEqual(3, response.ToolCalls.Count);
        Assert.AreEqual(3, response.ToolCalls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(response.ToolCalls.All(call => !string.IsNullOrWhiteSpace(call.Id)));
    }

    [TestMethod]
    public async Task ChatStreamAsync_StampsProviderDispatchAndHeadersTimings()
    {
        const string sse = """
            data: {"id":"chatcmpl-ttft","choices":[{"delta":{"content":"a"},"finish_reason":null}]}

            data: {"id":"chatcmpl-ttft","choices":[{"delta":{"content":"b"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => StreamingResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.HasCount(2, deltas);
        Assert.IsTrue(
            deltas.All(delta => delta.ProviderDispatchElapsedMs is not null),
            "Every delta must carry a dispatch-relative timestamp for TTFT attribution.");
        Assert.IsNotNull(deltas[0].ProviderHeadersMs);
        Assert.IsNull(deltas[1].ProviderHeadersMs, "Headers are reported once per provider response.");
        Assert.IsTrue(
            deltas[0].ProviderDispatchElapsedMs <= deltas[1].ProviderDispatchElapsedMs,
            "Dispatch-relative timestamps must be monotonic within one response.");
    }

    [TestMethod]
    public async Task ChatStreamAsync_ChunkWithoutModelPayload_DoesNotEmitDelta()
    {
        // A role-only first chunk is transport progress, not a model TTFT signal.
        const string sse = """
            data: {"id":"chatcmpl-role","choices":[{"delta":{"role":"assistant"},"finish_reason":null}]}

            data: {"id":"chatcmpl-role","choices":[{"delta":{"content":"answer"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var gateway = CreateGateway(_ => StreamingResponse(sse));

        var deltas = await ReadStreamAsync(gateway);

        Assert.HasCount(1, deltas);
        Assert.AreEqual("answer", deltas[0].ContentDelta);
        Assert.IsNotNull(deltas[0].ProviderHeadersMs, "The only emitted delta must still carry provider header timing.");
    }

    private static OpenAiLlmGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(
            new HttpClient(new StubHttpMessageHandler(send)),
            new LlmOptions("https://provider.example/v1", "test-key", "test-model"));

    private static HttpResponseMessage StreamingResponse(string sse)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };

    private static async Task<List<StreamDelta>> ReadStreamAsync(OpenAiLlmGateway gateway)
    {
        var deltas = new List<StreamDelta>();
        await foreach (var delta in gateway.ChatStreamAsync(
                           [new ChatMessage(ChatRole.User, "inspect")],
                           Array.Empty<ITool>()))
        {
            deltas.Add(delta);
        }

        return deltas;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
