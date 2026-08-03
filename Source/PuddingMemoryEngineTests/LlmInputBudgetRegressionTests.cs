using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class LlmInputBudgetRegressionTests
{
    [TestMethod]
    public void ResolveEffectiveInputLimit_UsesSmallestProviderOrReservedContextLimit()
    {
        var config = new LlmConfig
        {
            MaxContextTokens = 1_000_000,
            MaxInputTokens = 983_616,
            MaxOutputTokens = 4_096,
        };

        var limit = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(config);

        Assert.AreEqual(983_616, limit);
    }

    [TestMethod]
    public void RecordProviderUsage_ReplacesSmallerEstimateAndCalibratesNextRequest()
    {
        var store = new ContextUsageSnapshotStore();
        var first = store.CaptureLlmRequest(
            "session-calibration",
            [new ChatMessage(ChatRole.User, new string('a', 4_000))],
            tools: null,
            modelId: "qwen3.8-max-preview");

        var providerPromptTokens = first.RawEstimatedTokens * 2;
        var providerTotalTokens = providerPromptTokens + 128;
        var provider = store.RecordProviderUsage(
            "session-calibration",
            new TokenUsageDto
            {
                PromptTokens = providerPromptTokens,
                CompletionTokens = 128,
                TotalTokens = providerTotalTokens,
            });
        var next = store.CaptureLlmRequest(
            "session-calibration",
            [new ChatMessage(ChatRole.User, new string('a', 4_000))],
            tools: null,
            modelId: "qwen3.8-max-preview");

        Assert.AreEqual(providerTotalTokens, provider.UsedTokens);
        Assert.AreEqual("provider_usage", provider.Source);
        Assert.AreEqual(2.0, next.PromptCalibrationRatio, 0.001);
        Assert.AreEqual(next.RawEstimatedTokens * 2, next.UsedTokens);
    }

    [TestMethod]
    public void CaptureLlmRequest_CountsReasoningAndToolCallPayloads()
    {
        var store = new ContextUsageSnapshotStore();
        var plain = store.CaptureLlmRequest(
            "session-plain",
            [new ChatMessage(ChatRole.Assistant, "ok")],
            tools: null,
            modelId: "test-model");
        var withToolCall = store.CaptureLlmRequest(
            "session-tools",
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    "ok",
                    ToolCalls:
                    [
                        new ToolCall("call-1", "lookup", $"{{\"query\":\"{new string('x', 2_000)}\"}}"),
                    ],
                    ReasoningContent: new string('r', 1_000)),
            ],
            tools: null,
            modelId: "test-model");

        Assert.IsGreaterThan(plain.RawEstimatedTokens, withToolCall.RawEstimatedTokens);
    }

    [TestMethod]
    public void Prepare_TrimsOldConversationUnitsUntilCalibratedRequestFits()
    {
        var store = new ContextUsageSnapshotStore();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
        };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"request-{i} {new string('x', 800)}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"reply-{i} {new string('y', 800)}"));
        }

        var initial = store.CaptureLlmRequest("session-trim", messages, tools: null, modelId: "test-model");
        var config = new LlmConfig
        {
            ModelId = "test-model",
            MaxContextTokens = initial.UsedTokens,
            MaxInputTokens = initial.UsedTokens / 2,
            MaxOutputTokens = 1,
        };

        var result = LlmRequestBudgetGuard.Prepare(
            store,
            "session-trim",
            messages,
            tools: null,
            config,
            safetyBufferTokens: 0);

        Assert.IsGreaterThan(0, result.RemovedMessageCount);
        Assert.IsLessThanOrEqualTo(result.EffectiveInputLimit, result.Snapshot.UsedTokens);
        Assert.AreEqual(ChatRole.System, result.Messages[0].Role);
        Assert.AreEqual(messages[^1], result.Messages[^1]);
    }

    [TestMethod]
    public void ProviderInputLengthError_ParsesLimitAndRaisesConservativeCalibration()
    {
        var store = new ContextUsageSnapshotStore();
        store.CaptureLlmRequest(
            "session-rejection",
            [new ChatMessage(ChatRole.User, new string('z', 4_000))],
            tools: null,
            modelId: "qwen3.8-max-preview");

        var parsed = LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(
            "LLM API error (BadRequest): data: {\"error\":{\"code\":\"invalid_parameter_error\",\"param\":null,\"message\":\"Range of input length should be [1, 983616]\",\"type\":\"invalid_request_error\"},\"id\":\"chatcmpl-f7deb03d-6d9b-4a7e-a4dc-05663a1a5b3c\"}",
            out var maxInputTokens);
        store.RecordProviderInputLimitFailure("session-rejection", maxInputTokens);

        Assert.IsTrue(parsed);
        Assert.AreEqual(983_616, maxInputTokens);
        Assert.IsGreaterThan(1.0, store.GetPromptCalibrationRatio("session-rejection", "qwen3.8-max-preview"));
    }

    [TestMethod]
    public async Task DirectLlmClient_PropagatesResolvedMaxOutputTokensToProviderRequest()
    {
        var handler = new CapturingJsonHandler();
        var client = new DirectLlmClient(
            new FixedHttpClientFactory(new HttpClient(handler)),
            new TestLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance);

        await client.ChatAsync(
            "default",
            "session-output-budget",
            "template-1",
            [new ChatMessage(ChatRole.User, "hello")],
            llmConfig: new LlmConfig
            {
                Endpoint = "https://provider.test/v1",
                ApiKey = "test-key",
                ModelId = "test-model",
                MaxOutputTokens = 4_096,
            });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.AreEqual(4_096, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingJsonHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class TestLlmConfigService : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() =>
        [
            new LlmProviderInfo
            {
                ProviderId = "provider-a",
                Name = "Provider A",
                BaseUrl = "https://provider.test/v1",
                IsEnabled = true,
            },
        ];

        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [
            new LlmModelInfo
            {
                ProviderId = "provider-a",
                ModelId = "test-model",
                Name = "Test Model",
            },
        ];

        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => LlmProviderStrategy.Default;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => LlmProviderStrategy.Default;
        public void Reload(object config) { }
    }
}
