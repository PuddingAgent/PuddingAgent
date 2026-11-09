using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// CORE-I1-06 T1/T2: the real buffered transport (<see cref="DirectLlmClient"/> with a fake HTTP handler) is driven through
/// the real invocation facade. Every real HTTP send must open its own budget attempt: a retry is a new attempt, the first
/// failed send is unknown, the successful send settles, and a budget rejection stops the next send before the provider.
/// </summary>
[TestClass]
public sealed class TransportAttemptBudgetTests
{
    [TestMethod]
    public async Task BufferedRetry_ProducesOneAttemptPerRealSend()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, handler.Sends, "Two real provider sends are expected (one failure, one retry).");
        Assert.AreEqual(2, port.Begins, "Each real send must open its own budget attempt.");
        Assert.AreNotEqual(port.Claims[0].AttemptId, port.Claims[1].AttemptId);
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual("work:work-1", port.Claims[0].AccountId);
        Assert.AreEqual("Work", port.Claims[0].Identity.RootKind.ToString());
        Assert.AreEqual("work-1", port.Claims[0].Identity.RootId);
        Assert.AreEqual("rev-1", port.Claims[0].ModelRevision);
        Assert.AreEqual("pr-1", port.Claims[0].PriceVersion);
        Assert.AreEqual(10, port.LastUsage!.InputTokens);
        Assert.AreEqual(2, port.LastUsage.OutputTokens);
        Assert.IsTrue(port.UnknownReasons[0].StartsWith("llm.provider_failed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SuccessfulSend_SettlesExactlyOnceAndDoesNotRetry()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
        Assert.AreEqual("reservation-1", port.Grants[0].ReservationId);
    }

    [TestMethod]
    public async Task BudgetRejectionOnRetry_StopsBeforeTheSecondProviderSend()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var port = new CapturingPort { RejectFromAttempt = 2 };
        var facade = CreateFacade(handler, port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.Exhausted, result.ErrorCode);
        Assert.AreEqual(1, handler.Sends, "A rejected retry must not reach the provider.");
        Assert.AreEqual(2, port.Begins, "Both attempts were requested; the second was rejected before dispatch.");
        Assert.AreEqual(1, port.Claims.Count, "Only the first attempt was admitted.");
        Assert.AreEqual(1, port.Unknowns, "The dispatched first attempt stays unknown; it is never refunded.");
    }

    [TestMethod]
    public async Task StreamingSend_OpensOneAttemptAndSettlesTheReportedUsage()
    {
        var handler = new SseHandler();
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        var deltas = new List<StreamDelta>();
        await foreach (var delta in facade.InvokeStreamAsync(Request())) deltas.Add(delta);

        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(1, port.Settles, "unknowns=" + string.Join(",", port.UnknownReasons));
        Assert.AreEqual(0, port.Unknowns, string.Join(",", port.UnknownReasons));
        Assert.IsTrue(deltas.Any(d => d.ContentDelta == "ok"));
        Assert.AreEqual(10, port.LastUsage!.InputTokens);
        Assert.AreEqual(2, port.LastUsage.OutputTokens);
    }

    [TestMethod]
    public async Task StreamingBudgetRejection_StopsBeforeTheProvider()
    {
        var handler = new SseHandler();
        var port = new CapturingPort { RejectFromAttempt = 1 };
        var facade = CreateFacade(handler, port);

        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
        {
            await foreach (var _ in facade.InvokeStreamAsync(Request())) { }
        });

        Assert.AreEqual(BudgetErrorCodes.Exhausted, failure.Code);
        Assert.AreEqual(0, handler.Sends, "A rejected streaming attempt must not reach the provider.");
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(0, port.Unknowns);
    }

    [TestMethod]
    public async Task StreamingRetryBeforeFirstDelta_OpensANewAttempt()
    {
        var handler = new SseHandler(failFirst: 1);
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        var deltas = new List<StreamDelta>();
        await foreach (var delta in facade.InvokeStreamAsync(Request())) deltas.Add(delta);

        Assert.AreEqual(2, handler.Sends, "The transport retried before the first delta.");
        Assert.AreEqual(2, port.Begins, "Each stream attempt must be its own budget attempt.");
        Assert.AreNotEqual(port.Claims[0].AttemptId, port.Claims[1].AttemptId);
        Assert.AreEqual(1, port.Unknowns, "The failed first attempt stays unknown.");
        Assert.AreEqual(1, port.Settles, "The successful retry settles exactly once.");
        Assert.IsTrue(deltas.Any(d => d.ContentDelta == "ok"));
    }

    [TestMethod]
    public async Task StreamingRetryRejectedByBudget_StopsBeforeTheSecondProviderSend()
    {
        var handler = new SseHandler(failFirst: 1);
        var port = new CapturingPort { RejectFromAttempt = 2 };
        var facade = CreateFacade(handler, port);

        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
        {
            await foreach (var _ in facade.InvokeStreamAsync(Request())) { }
        });

        Assert.AreEqual(BudgetErrorCodes.Exhausted, failure.Code);
        Assert.AreEqual(1, handler.Sends, "A rejected retry must not reach the provider.");
        Assert.AreEqual(2, port.Begins);
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task StreamingCancelledBeforeFirstDelta_DoesNotReachTheProvider()
    {
        var handler = new SseHandler();
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in facade.InvokeStreamAsync(Request(), cts.Token)) { }
        });

        Assert.AreEqual(0, handler.Sends, "A cancelled stream must not send to the provider.");
        Assert.AreEqual(0, port.Settles);
    }


    [TestMethod]
    public async Task MissingUsage_LeavesTheAttemptUnknownInsteadOfZero()
    {
        var handler = new NoUsageHandler();
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(0, port.Settles, "A response without usage must never settle as zero.");
        Assert.IsTrue(port.UnknownReasons.Contains("llm.usage_unknown"), string.Join(",", port.UnknownReasons));
    }

    [TestMethod]
    public async Task MissingBudgetPort_RefusesBeforeAnyProviderSend()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: null);

        var result = await facade.InvokeAsync(Request());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, result.ErrorCode);
        Assert.AreEqual(0, handler.Sends, "A managed attempt without the budget port must not reach the provider.");
    }

    [TestMethod]
    public async Task NonParticipatingClient_KeepsTheLegacySingleAttempt()
    {
        var client = new SimpleClient();
        var port = new CapturingPort();
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual(1, port.Begins, "Only the per-send transport owns attempts; other clients keep the legacy gate.");
        Assert.AreEqual(1, port.Settles);
    }

    private sealed class NoUsageHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            const string body =
                "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model-a\"," +
                "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SimpleClient : IRuntimeLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse("ok", [], null,
                new TokenUsageDto { PromptTokens = 10, CompletionTokens = 2 }, null));
        }

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
    [TestMethod]
    public async Task ResponsesBufferedSend_IsOneAttemptSettledByTheGateway()
    {
        var handler = new ResponsesHandler();
        var port = new CapturingPort();
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(protocol: "responses"),
            NullLogger<DirectLlmClient>.Instance);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, port.Begins, "The Responses gateway owns exactly one attempt for one buffered send.");
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
        Assert.AreEqual(10, port.LastUsage!.InputTokens);
        Assert.AreEqual(2, port.LastUsage.OutputTokens);
    }

    private sealed class ResponsesHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            const string body =
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":" +
                "[{\"type\":\"output_text\",\"text\":\"ok\"}]}]," +
                "\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
    [TestMethod]
    public async Task ConsumerAbandonsStreamAfterFirstDelta_AttemptStaysUnknown()
    {
        var handler = new MultiDeltaHandler();
        var port = new CapturingPort();
        var facade = CreateFacade(handler, port);

        await foreach (var delta in facade.InvokeStreamAsync(Request()))
        {
            if (delta.ContentDelta == "a")
                break;
        }

        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(0, port.Settles, "An abandoned stream must not settle as if it completed.");
        Assert.IsTrue(port.UnknownReasons.Count >= 1, "The sent attempt must be recorded as unknown.");
    }

    private sealed class MultiDeltaHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            const string body =
                "data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"b\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
    [TestMethod]
    public async Task ConcurrencyPort_IsAcquiredBeforeAndReleasedAfterTheRealSend()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort();
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance,
            concurrencyPort: slots);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, slots.Acquired);
        Assert.AreEqual(1, slots.Released);
        Assert.AreEqual(1, handler.Sends);
    }

    [TestMethod]
    public async Task ConcurrencySlot_IsReleasedWhenTheConsumerAbandonsTheStream()
    {
        var handler = new MultiDeltaHandler();
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort();
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance,
            concurrencyPort: slots);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        await foreach (var delta in facade.InvokeStreamAsync(Request()))
        {
            if (delta.ContentDelta == "a")
                break;
        }

        Assert.AreEqual(1, slots.Acquired);
        Assert.AreEqual(1, slots.Released, "The slot must be returned once the abandoned attempt ends.");
    }

    [TestMethod]
    public async Task RevokedGeneration_IsRejectedBeforeTheProvider()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort { AcquireError = new ModelException(ModelErrorCodes.LeaseGenerationRevoked, "revoked") };
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance,
            concurrencyPort: slots);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await facade.InvokeAsync(Request());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, handler.Sends);
        Assert.AreEqual(0, port.Begins);
    }

    private sealed class FakeConcurrencyPort : IModelAttemptConcurrencyPort
    {
        public ModelException? AcquireError { get; init; }
        public int Acquired { get; private set; }
        public int Released { get; private set; }

        public ValueTask<IModelAttemptConcurrencyLease> AcquireAsync(string modelId, CancellationToken ct = default)
        {
            if (AcquireError is not null)
                throw AcquireError;
            Acquired++;
            return ValueTask.FromResult<IModelAttemptConcurrencyLease>(new FakeLease(this));
        }

        private sealed class FakeLease(FakeConcurrencyPort owner) : IModelAttemptConcurrencyLease
        {
            private int _released;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    owner.Released++;
                return ValueTask.CompletedTask;
            }
        }
    }
    [TestMethod]
    public async Task Slot_IsHeldUntilTheInFlightSendExits()
    {
        var handler = new BlockingHandler();
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort();
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(),
            NullLogger<DirectLlmClient>.Instance,
            concurrencyPort: slots);
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var sendTask = facade.InvokeAsync(Request());
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, slots.Acquired);
        Assert.AreEqual(0, slots.Released, "The slot must stay held while the provider request is in flight.");

        handler.Release();
        var result = await sendTask;
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, slots.Released, "The slot is released only after the in-flight request exits.");
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            const string body =
                "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model-a\"," +
                "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    [TestMethod]
    public async Task ResponsesStreaming_StillHoldsTheConcurrencySlot()
    {
        var handler = new ResponsesSseHandler();
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort();
        var facade = CreateFacade(handler, port, slots, protocol: "responses");

        await foreach (var _ in facade.InvokeStreamAsync(Request()))
        {
        }

        Assert.AreEqual(1, handler.Sends);
        Assert.AreEqual(1, slots.Acquired, "Marker-gateway streaming must still take the transport lease.");
        Assert.AreEqual(1, slots.Released);
    }

    [TestMethod]
    public async Task ResponsesStreaming_RevokedGeneration_DoesNotReachProvider()
    {
        var handler = new ResponsesSseHandler();
        var port = new CapturingPort();
        var slots = new FakeConcurrencyPort
        {
            AcquireError = new ModelException(ModelErrorCodes.LeaseGenerationRevoked, "revoked"),
        };
        var facade = CreateFacade(handler, port, slots, protocol: "responses");

        await Assert.ThrowsExactlyAsync<ModelException>(async () =>
        {
            await foreach (var _ in facade.InvokeStreamAsync(Request()))
            {
            }
        });
        Assert.AreEqual(0, handler.Sends);
    }

    private sealed class ResponsesSseHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            const string body =
                "data:{\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n" +
                "data:{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":1,\"total_tokens\":4}}}\n\n" +
                "data:[DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static LlmInvocationService CreateFacade(HttpMessageHandler handler, CapturingPort port,
        FakeConcurrencyPort? slots = null, string protocol = "openai")
    {
        var client = new DirectLlmClient(
            new FakeHttpClientFactory(handler),
            new FakeLlmConfigService(protocol),
            NullLogger<DirectLlmClient>.Instance,
            concurrencyPort: slots);
        return new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
    }

    private static LlmInvocationRequest Request()
    {
        var binding = new LlmAttemptBudget(
            "work:work-1", [], "Work", "work-1", "ws-1", "agent-1", "session-1", "attempt-base", "rev-1",
            "pr-1", 128_000, 8_192, "usd");
        return new()
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            AgentTemplateId = "template-1",
            Profile = new LlmInvocationProfile { ProviderId = "provider-a", ProfileId = "profile-a", ModelId = "model-a" },
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ConfigOverride = new LlmConfig
            {
                Endpoint = "http://llm.test/v1",
                ApiKey = "test-key",
                ModelId = "model-a",
                MaxOutputTokens = 4_096,
                Protocol = "openai",
            },
            BudgetAttempt = binding,
        };
    }

    private static string OkBody() =>
        "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model-a\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}";

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            var status = _index < statuses.Length ? statuses[_index] : statuses[^1];
            _index++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? OkBody() : "{\"error\":\"boom\"}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SseHandler(int failFirst = 0) : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            if (Sends <= failFirst)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json"),
                });
            }
            const string body =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n" +
                "data: [DONE]\n\n";
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public sealed class FakeLlmConfigService(string protocol = "openai") : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() =>
        [
            new() { ProviderId = "provider-a", Name = "provider-a", BaseUrl = "http://llm.test", IsEnabled = true },
        ];

        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [
            new()
            {
                ModelId = "model-a", ProviderId = "provider-a", Name = "model-a", Protocol = protocol,
                MaxContextTokens = 128_000, MaxOutputTokens = 8_192,
            },
        ];

        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) =>
            new() { MaxRetries = 1, RetryDelaySeconds = 0 };
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }

    private sealed class CapturingPort : IModelAttemptBudgetPort
    {
        public int RejectFromAttempt { get; init; }
        public int Begins { get; private set; }
        public int Settles { get; private set; }
        public int Unknowns => UnknownReasons.Count;
        public List<ModelAttemptBudgetClaim> Claims { get; } = [];
        public List<ModelAttemptBudgetGrant> Grants { get; } = [];
        public List<string> UnknownReasons { get; } = [];
        public ModelUsage? LastUsage { get; private set; }

        public ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
            CancellationToken ct = default)
        {
            Begins++;
            if (RejectFromAttempt > 0 && Begins >= RejectFromAttempt)
                throw new ModelAttemptBudgetException(BudgetErrorCodes.Exhausted, "budget exhausted");
            Claims.Add(claim);
            var grant = new ModelAttemptBudgetGrant("reservation-1", claim.AttemptId, Shadow: false, OwnerEpoch: 1,
                CancellationRevision: 0, InputPerMillion: 1m, CacheHitPerMillion: 0.5m, OutputPerMillion: 2m,
                BaseUnit: 1_000_000, CostUnit: "usd");
            Grants.Add(grant);
            return ValueTask.FromResult<ModelAttemptBudgetGrant?>(grant);
        }

        public ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
            CancellationToken ct = default)
        {
            Settles++;
            LastUsage = usage;
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkUnknownAsync(ModelAttemptBudgetGrant grant, string reasonCode,
            CancellationToken ct = default)
        {
            UnknownReasons.Add(reasonCode);
            return ValueTask.CompletedTask;
        }
    }
}
