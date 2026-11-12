using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Operators.PublicApi;
using PuddingRuntime.Services;

namespace PuddingModelEvaluation.IntegrationTests;

[TestClass]
public sealed class ConfiguredModelTests
{
    [TestMethod]
    public async Task FrozenRoute_SendsOneBoundedRequest_RecordsActualUsageOnce()
    {
        var config = new Config(); var wire = new Wire(); var recorder = new Recorder();
        var binder = Binder(config, wire, recorder);
        var model = await binder.BindAsync(Context(), default);
        config.Info = config.Info with { OutputPricePer1MTokens = 999 };
        using var parent = Parent();
        var receipt = await new ModelResourcePool().InvokeAsync(parent, model, new("test", "system", "input", 64));
        Assert.AreEqual(1, wire.Calls); Assert.AreEqual(1, recorder.Calls);
        Assert.AreEqual(64, wire.Request.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("model", wire.Request.GetProperty("model").GetString());
        Assert.IsFalse(wire.Request.TryGetProperty("tools", out _));
        Assert.AreEqual(.00012m, receipt.Cost); Assert.AreEqual(receipt.Cost, parent.Snapshot().Cost);
        Assert.AreEqual(10L, receipt.Usage.OutputTokens); Assert.AreEqual("model-evaluation", recorder.Operation);
    }
    [TestMethod]
    public async Task HttpFailureIsNotRetried_AndDoesNotRefundDispatchedReservation()
    {
        var config = new Config(); var wire = new Wire { Status = HttpStatusCode.TooManyRequests }; var recorder = new Recorder();
        var model = await Binder(config, wire, recorder).BindAsync(Context(), default); using var parent = Parent();
        var error = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => new ModelResourcePool().InvokeAsync(parent, model, new("test", "system", "input", 64)));
        Assert.AreEqual("model.failed", error.Code); Assert.AreEqual(1, wire.Calls); Assert.AreEqual(0, recorder.Calls);
        Assert.AreEqual(64L, parent.Snapshot().UnknownOutputTokens); Assert.AreEqual("budget.usage_unknown", parent.Snapshot().FailureCode);
        Assert.DoesNotContain("offline-fixture", error.Message);
    }
    [TestMethod]
    public async Task PrimaryModelGateAndCancellationAreShared_NoRequestBeforeAcquisition()
    {
        var config = new Config(); var wire = new Wire(); var limiter = new ProviderRateLimiter(config, NullLogger<ProviderRateLimiter>.Instance);
        using var occupied = await limiter.AcquireAsync("provider", "model");
        var binder = new ConfiguredExecutionModelBinder(config, wire, limiter, null!, new Recorder());
        var model = await binder.BindAsync(Context(), default); using var parent = Parent();
        Assert.AreEqual("model.timeout", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => new ModelResourcePool().InvokeAsync(parent, model, new("test", "system", "input", 64, 50)))).Code);
        Assert.AreEqual(0, wire.Calls); Assert.AreEqual(0L, parent.Snapshot().UnknownOutputTokens);
        for (var i = 0; i < 100 && parent.Snapshot().ActiveLeases != 0; i++) await Task.Delay(10);
        Assert.AreEqual(0, parent.Snapshot().ActiveLeases);
    }
    [TestMethod]
    public async Task NoImplicitModel_AndUnsupportedPricingFailBeforeAnyTransport()
    {
        var config = new Config(); var wire = new Wire(); var binder = Binder(config, wire, new Recorder());
        Assert.AreEqual("model.not_configured", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => binder.BindAsync(new() { WorkspaceId = "w", SessionId = "s", AgentInstanceId = "a" }, default))).Code);
        config.Info = config.Info with { PriceWindows = [new()] };
        Assert.AreEqual("model.pricing_unsupported", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => binder.BindAsync(Context(), default))).Code);
        config.Info = config.Info with { PriceWindows = [], InputPricePer1MTokens = 0, OutputPricePer1MTokens = 0 };
        Assert.AreEqual("model.pricing_unavailable", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => binder.BindAsync(Context(), default))).Code);
        Assert.AreEqual(0, wire.Calls);
    }
    private static ConfiguredExecutionModelBinder Binder(Config c, Wire w, Recorder r) => new(c, w, new(c, NullLogger<ProviderRateLimiter>.Instance), null!, r);
    private static ToolExecutionContext Context() => new() { WorkspaceId = "w", AgentInstanceId = "a", SessionId = "s", CallerLlmSnapshot = new("provider", "model", "openai", []) };
    private static ExecutionResourceScope Parent() => new(new("w", "a", "s", "run"), new(512, 1, 4), DateTimeOffset.UtcNow.AddMinutes(1));
    private sealed class Config : ILlmConfigService
    {
        public LlmModelInfo Info = new() { ProviderId = "provider", ModelId = "model", Protocol = "openai", MaxInputTokens = 8192, MaxOutputTokens = 2048, InputPricePer1MTokens = 1, OutputPricePer1MTokens = 2, CacheHitPricePer1MTokens = 1 };
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [new() { ProviderId = "provider", IsEnabled = true }];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [Info];
#pragma warning disable CS0618
        public LlmConfig? Resolve(string providerId, string modelId) => new() { Endpoint = "https://offline.invalid/v1", ApiKey = "offline-fixture", ModelId = "model" };
#pragma warning restore CS0618
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => new() { MaxConcurrentRequests = 1, MaxRetries = 5 };
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => GetModelStrategy(providerId, "model");
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public void Reload(object config) => throw new NotSupportedException();
    }
    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls; public JsonElement Request; public HttpStatusCode Status = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new(Status) { Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10,\"total_tokens\":110}}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Recorder : ILlmGatewayUsageRecorder
    {
        public int Calls; public string? Operation;
        public Task RecordRequiredAsync(TokenUsageDto usage, string sourceId, string operation, string workspaceId, string sessionId, string agentTemplateId, string providerId, string modelId, DateTimeOffset occurredAtUtc, CancellationToken ct = default)
        { Calls++; Operation = operation; return Task.CompletedTask; }
    }
}
