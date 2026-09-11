using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-A：Composition 提交（执行状态）与 telemetry（best-effort）解耦。
///
/// 修复前 `RecordCompositionSnapshotAsync` 以 `if (_telemetrySink is null) return;` 早退，
/// 无 telemetry 时 <c>_compositionVersions.Observe(...)</c>（revision 自增 + 写穿持久化）完全不执行。
/// 本测试锁定：sink 的有无、sink 是否抛异常，都不得改变 Composition 提交语义；
/// 且提交必须发生在遥测之前。
/// </summary>
[TestClass]
public sealed class CompositionCommitDecouplingTests
{
    [TestMethod]
    public async Task ChatAsync_WithoutTelemetrySink_StillObservesCompositionVersion()
    {
        var registry = new RecordingCompositionVersionRegistry();
        var client = CreateClient(registry, telemetrySink: null);

        var response = await client.ChatAsync(
            "default",
            "session-no-telemetry",
            "template-1",
            [new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        Assert.AreEqual("ok", response.Content);
        Assert.AreEqual(1, registry.ObserveCount, "无 telemetry sink 时 Composition 提交（Observe）必须照常执行。");
        Assert.AreEqual(1, registry.LastObservation.Version);
    }

    [TestMethod]
    public async Task ChatAsync_WithoutTelemetrySink_StillWritesThroughCompositionRecord()
    {
        var store = new CountingCompositionStore();
        var registry = new PersistentCompositionVersionRegistry(store);
        var client = CreateClient(registry, telemetrySink: null);

        await client.ChatAsync(
            "default",
            "session-write-through",
            "template-1",
            [new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        // 写穿是异步 fire-and-forget，留窗口等待落库。
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (store.AppendCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.AreEqual(1, store.AppendCount, "无 telemetry sink 时 Composition 写穿持久化必须照常发生。");
        Assert.AreEqual("session-write-through", store.LastRecord!.SessionId);
        Assert.AreEqual(0, registry.WriteThroughFailureCount, "正常写穿不得计为失败。");
    }

    [TestMethod]
    public async Task ChatAsync_WithoutTelemetry_AdvancesRevisionOnPromptChange()
    {
        var registry = new RecordingCompositionVersionRegistry();
        var client = CreateClient(registry, telemetrySink: null);

        await client.ChatAsync(
            "default",
            "session-revision",
            "template-1",
            [new ChatMessage(ChatRole.System, "sys-a"), new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        await client.ChatAsync(
            "default",
            "session-revision",
            "template-1",
            [new ChatMessage(ChatRole.System, "sys-b"), new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        Assert.AreEqual(2, registry.ObserveCount);
        Assert.AreEqual(2, registry.LastObservation.Version, "无 telemetry 时 revision 仍必须随组合变化单调递增。");
    }

    [TestMethod]
    public async Task ChatAsync_WithThrowingTelemetrySink_CommitStillHappensAndRequestSucceeds()
    {
        var registry = new RecordingCompositionVersionRegistry();
        var sink = new ThrowingCompositionTelemetrySink();
        var client = CreateClient(registry, telemetrySink: sink);

        var response = await client.ChatAsync(
            "default",
            "session-throwing-sink",
            "template-1",
            [new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        Assert.AreEqual("ok", response.Content, "composition 遥测 sink 抛异常不得让请求失败。");
        Assert.AreEqual(1, registry.ObserveCount, "composition 遥测 sink 抛异常不得影响已完成的 Composition 提交。");
        Assert.AreEqual(1, sink.CompositionAttempts, "遥测确实被尝试上报过一次（异常被 best-effort 吞下）。");
    }

    [TestMethod]
    public async Task ChatAsync_CommitsBeforeRecordingCompositionTelemetry()
    {
        var order = new List<string>();
        var registry = new RecordingCompositionVersionRegistry(order);
        var sink = new OrderRecordingTelemetrySink(order);
        var client = CreateClient(registry, sink);

        await client.ChatAsync(
            "default",
            "session-order",
            "template-1",
            [new ChatMessage(ChatRole.User, "hello")],
            llmConfig: TestConfig());

        CollectionAssert.AreEqual(
            new[] { "commit", "telemetry:composition_snapshot" },
            order.Take(2).ToArray());

        var metric = sink.Metrics.Single(m => m.Name == "composition_snapshot");
        Assert.AreEqual("1", metric.Dimensions!["composition_version"]);
        Assert.AreEqual("session-order", metric.Dimensions["session_id"]);
    }

    // ── 测试替身 ───────────────────────────────────────

    private static LlmConfig TestConfig() => new()
    {
        Endpoint = "https://provider.test/v1",
        ApiKey = "test-key",
        ModelId = "test-model",
    };

    private static DirectLlmClient CreateClient(
        ICompositionVersionRegistry registry,
        ITelemetryMetricSink? telemetrySink) =>
        new(
            new FixedHttpClientFactory(new HttpClient(new OkChatHandler())),
            new TestLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance,
            telemetrySink: telemetrySink,
            compositionVersions: registry);

    /// <summary>真实内存语义 + 调用计数/调用序记录的合成 registry（避免断言与实现耦合）。</summary>
    private sealed class RecordingCompositionVersionRegistry : ICompositionVersionRegistry
    {
        private readonly CompositionVersionRegistry _inner = new();
        private readonly List<string>? _order;

        public RecordingCompositionVersionRegistry(List<string>? order = null) => _order = order;

        public int ObserveCount { get; private set; }

        public CompositionObservation LastObservation { get; private set; }

        public CompositionObservation Observe(
            string sessionId,
            string systemPromptHash,
            string toolSpecHash,
            IReadOnlyList<string>? toolIds = null,
            int permissionEpoch = 0,
            string? skillManifestHash = null,
            string? permissionFingerprint = null,
            string? canonicalSystemPrefixHash = null)
        {
            ObserveCount++;
            _order?.Add("commit");
            LastObservation = _inner.Observe(
                sessionId, systemPromptHash, toolSpecHash, toolIds, permissionEpoch,
                skillManifestHash, permissionFingerprint);
            return LastObservation;
        }
    }

    private sealed class CountingCompositionStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public int AppendCount => _records.Count;

        public SessionCompositionRecord? LastRecord => _records.Count == 0 ? null : _records[^1];

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_records.Count == 0 ? null : _records[^1]);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(_records.ToArray());
    }

    /// <summary>
    /// 只对 composition 遥测抛异常的 sink。
    /// 限定范围的原因：其他遥测路径（llm/activity metric）不在 C01-A 范围内，
    /// 且实测仍会让请求失败（DirectLlmClient.cs:995/1042），已作为独立发现上报。
    /// </summary>
    private sealed class ThrowingCompositionTelemetrySink : ITelemetryMetricSink
    {
        public int CompositionAttempts { get; private set; }

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            if (metric.Name != "composition_snapshot")
                return Task.CompletedTask;

            CompositionAttempts++;
            throw new InvalidOperationException("telemetry sink boom");
        }
    }

    private sealed class OrderRecordingTelemetrySink(List<string> order) : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            order.Add($"telemetry:{metric.Name}");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OkChatHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class TestLlmConfigService : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() =>
        [
            new()
            {
                ProviderId = "provider-a",
                Name = "Provider A",
                BaseUrl = "https://provider.test/v1",
                IsEnabled = true,
                HasApiKey = true,
            },
        ];

        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [
            new()
            {
                ProviderId = "provider-a",
                ModelId = "test-model",
                Protocol = "openai",
                CapabilityTags = new[] { "text" }.ToList(),
            },
        ];

        public LlmConfig? Resolve(string providerId, string modelId) => null;

        public LlmProfileInfo? ResolveProfile(string profileId) => null;

        public LlmConfig? GetMemoryConfig() => null;

        public LlmConfig? GetEmbeddingConfig() => null;

        public LlmProviderStrategy? GetProviderStrategy(string providerId) => new()
        {
            StreamTimeoutSeconds = 300,
            MaxConcurrentRequests = 50,
            MaxRetries = 0,
            RetryDelaySeconds = 0,
        };

        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;

        public void Reload(object config)
        {
        }
    }
}
