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

    /// <summary>
    /// R1：实测**不再改写准备快照**（旧行为是把它换成 Provider Total），
    /// 而是按身份配对、用**该请求自己的** raw 学习保守校准，并用校准后的值保护下一次请求。
    /// </summary>
    [TestMethod]
    public void RecordProviderUsage_CalibratesNextRequestFromItsOwnRaw_WithoutReplacingPreparedSnapshot()
    {
        var store = new ContextUsageSnapshotStore();
        var invocationId = "session-calibration:trace:1:agent";
        var attemptId = invocationId + ":a0";
        var first = store.CaptureLlmRequest(
            "session-calibration",
            [new ChatMessage(ChatRole.User, new string('a', 4_000))],
            tools: null,
            modelId: "qwen3.8-max-preview",
            purpose: LlmCallPurposes.Agent,
            invocationId: invocationId,
            attemptId: attemptId);

        var providerPromptTokens = first.RawEstimatedTokens * 2;
        var providerTotalTokens = providerPromptTokens + 128;
        var returned = store.RecordProviderUsage(
            "session-calibration",
            new TokenUsageDto
            {
                PromptTokens = providerPromptTokens,
                CompletionTokens = 128,
                TotalTokens = providerTotalTokens,
            },
            purpose: LlmCallPurposes.Agent,
            invocationId: invocationId,
            attemptId: attemptId);
        var next = store.CaptureLlmRequest(
            "session-calibration",
            [new ChatMessage(ChatRole.User, new string('a', 4_000))],
            tools: null,
            modelId: "qwen3.8-max-preview");

        // 准备槽位保持原样（不再被 Provider Total 顶替）。注意：必须在**第二次 Capture 之前**断言，
        // 因为第二次 Capture 本身就是一次新的准备发布。
        Assert.AreSame(first, returned, "主 usage 不得改写准备快照");

        // 实测按身份配对，raw 来自它自己那次请求
        Assert.IsTrue(store.TryGetLastMeasuredRequest("session-calibration", out var measured));
        Assert.AreEqual(providerPromptTokens, measured!.ProviderPromptTokens);
        Assert.AreEqual(first.RawEstimatedTokens, measured.RawEstimatedTokens);

        // 校准仍生效（2 倍），下一次准备按校准后的值保护
        Assert.AreEqual(2.0, next.PromptCalibrationRatio, 0.001);
        Assert.AreEqual(next.RawEstimatedTokens * 2, next.UsedTokens);
        Assert.IsTrue(store.TryGet("session-calibration", out var current));
        Assert.AreSame(next, current, "当前准备槽位应是最近一次准备发布的对象");
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
                Protocol = "openai",
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
