using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class LlmStreamObservabilityTests
{
    [TestMethod]
    public async Task ChatStreamAsync_WhenProviderNeverYieldsChunk_RecordsFirstChunkWaitMetric()
    {
        var telemetry = new RecordingTelemetrySink();
        var client = new DirectLlmClient(
            new FixedHttpClientFactory(new HttpClient(new HangingStreamHandler())),
            new TestLlmConfigService(streamTimeoutSeconds: 1),
            NullLogger<DirectLlmClient>.Instance,
            telemetrySink: telemetry);

        try
        {
            await foreach (var _ in client.ChatStreamAsync(
                               "default",
                               "session-1",
                               "template-1",
                               [new ChatMessage(ChatRole.User, "hello")],
                               llmConfig: new LlmConfig
                               {
                                   Endpoint = "https://provider.test/v1",
                                   ApiKey = "test-key",
                                   ModelId = "test-model",
                               }))
            {
            }
        }
        catch (OperationCanceledException)
        {
        }

        var firstChunkMetric = telemetry.Metrics.SingleOrDefault(m => m.Name == "llm.stream.provider_first_chunk_wait");
        Assert.IsNotNull(firstChunkMetric);
        Assert.AreEqual(TelemetryMetricStatuses.Failed, firstChunkMetric.Status);
        Assert.AreEqual("false", firstChunkMetric.Dimensions!["first_chunk_received"]);
        Assert.AreEqual("true", firstChunkMetric.Dimensions["stream_no_chunks"]);

        var terminalMetric = telemetry.Metrics.Last(m => m.Name == "llm.chat_stream");
        Assert.AreEqual(TelemetryMetricStatuses.Failed, terminalMetric.Status);
        Assert.AreEqual("true", terminalMetric.Dimensions!["stream_no_chunks"]);
        Assert.IsGreaterThanOrEqualTo(900L, long.Parse(terminalMetric.Dimensions["stream_first_chunk_wait_ms"]));
    }

    [TestMethod]
    public async Task ProviderRateLimiter_AcquireAsync_ReturnsStructuredLeaseDiagnostics()
    {
        var limiter = new ProviderRateLimiter(
            new TestLlmConfigService(maxConcurrentRequests: 1),
            NullLogger<ProviderRateLimiter>.Instance);

        using var lease = await limiter.AcquireAsync("provider-a", "model-a");

        var leaseType = lease.GetType();
        Assert.IsNotNull(leaseType.GetProperty("WaitMs"));
        Assert.IsNotNull(leaseType.GetProperty("MaxConcurrent"));
        Assert.IsNotNull(leaseType.GetProperty("AvailableBeforeAcquire"));
        Assert.IsNotNull(leaseType.GetProperty("AvailableAfterAcquire"));
    }

    [TestMethod]
    public async Task ChatStreamAsync_WhenRateLimitWaitTimesOut_RecordsRateLimitWaitMetric()
    {
        var telemetry = new RecordingTelemetrySink();
        var configService = new TestLlmConfigService(streamTimeoutSeconds: 1, maxConcurrentRequests: 1);
        var limiter = new ProviderRateLimiter(configService, NullLogger<ProviderRateLimiter>.Instance);
        using var heldLease = await limiter.AcquireAsync("provider-a", "test-model");
        var client = new DirectLlmClient(
            new FixedHttpClientFactory(new HttpClient(new HangingStreamHandler())),
            configService,
            NullLogger<DirectLlmClient>.Instance,
            telemetrySink: telemetry,
            rateLimiter: limiter);

        try
        {
            await foreach (var _ in client.ChatStreamAsync(
                               "default",
                               "session-1",
                               "template-1",
                               [new ChatMessage(ChatRole.User, "hello")],
                               llmConfig: new LlmConfig
                               {
                                   Endpoint = "https://provider.test/v1",
                                   ApiKey = "test-key",
                                   ModelId = "test-model",
                               }))
            {
            }
        }
        catch (OperationCanceledException)
        {
        }

        var rateLimitMetric = telemetry.Metrics.SingleOrDefault(m => m.Name == "llm.rate_limit.wait");
        Assert.IsNotNull(rateLimitMetric);
        Assert.AreEqual(TelemetryMetricStatuses.Failed, rateLimitMetric.Status);
        Assert.AreEqual("false", rateLimitMetric.Dimensions!["rate_limit_acquired"]);
        Assert.AreEqual("provider-a", rateLimitMetric.Dimensions["rate_limit_provider_id"]);
        Assert.AreEqual("test-model", rateLimitMetric.Dimensions["rate_limit_model"]);
        Assert.IsGreaterThanOrEqualTo(900L, rateLimitMetric.DurationMs.GetValueOrDefault());

        var terminalMetric = telemetry.Metrics.Last(m => m.Name == "llm.chat_stream");
        Assert.AreEqual(TelemetryMetricStatuses.Failed, terminalMetric.Status);
        Assert.AreEqual("true", terminalMetric.Dimensions!["rate_limit_waited"]);
        Assert.IsGreaterThanOrEqualTo(900L, long.Parse(terminalMetric.Dimensions["rate_limit_wait_ms"]));
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class HangingStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new HangingStreamContent(),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class HangingStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new HangingStream());
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTelemetrySink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed class TestLlmConfigService(
        int streamTimeoutSeconds = 300,
        int maxConcurrentRequests = 50) : ILlmConfigService
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

        public IReadOnlyList<LlmModelInfo> GetAllModels() => [];

        public LlmConfig? Resolve(string providerId, string? modelId = null) => null;

        public LlmProfileInfo? ResolveProfile(string profileId) => null;

        public LlmProfileInfo GetDefaultProfile() => new()
        {
            ProfileId = "default-conscious",
            ProviderId = "provider-a",
            ModelId = "test-model",
            Config = GetDefault(),
        };

        public LlmConfig GetDefault() => new()
        {
            Endpoint = "https://provider.test/v1",
            ApiKey = "test-key",
            ModelId = "test-model",
        };

        public LlmConfig? GetMemoryConfig() => null;

        public LlmConfig? GetEmbeddingConfig() => null;

        public LlmProviderStrategy? GetProviderStrategy(string providerId) => new()
        {
            StreamTimeoutSeconds = streamTimeoutSeconds,
            MaxConcurrentRequests = maxConcurrentRequests,
        };

        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;

        public void Reload(object config)
        {
        }
    }
}
