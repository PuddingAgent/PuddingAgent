using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Core;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// PA-01 P2 at the shared LLM invocation boundary: a budget identity gates the provider call; success settles by actual
/// usage, missing usage marks unknown, provider failure marks unknown, and a rejected gate never calls the provider.
/// A request without a budget identity keeps the legacy path untouched.
/// </summary>
[TestClass]
public sealed class LlmInvocationServiceBudgetTests
{
    [TestMethod]
    public async Task BudgetedSuccess_ReservesAndSettlesTheActualUsage()
    {
        var client = new FakeClient(Response(usage: new TokenUsageDto { PromptTokens = 1_000, CompletionTokens = 200, PromptCacheHitTokens = 400 }));
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await service.InvokeAsync(Request(withBudget: true));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
        Assert.AreEqual(1_000L, port.LastUsage!.InputTokens);
        Assert.AreEqual(200L, port.LastUsage.OutputTokens);
        Assert.AreEqual(400L, port.LastUsage.CacheHitTokens);
        Assert.AreEqual(1, client.Calls);
    }

    [TestMethod]
    public async Task RejectedGate_FailsWithoutCallingTheProvider()
    {
        var client = new FakeClient(Response());
        var port = new FakeBudgetPort { BeginFailure = new ModelAttemptBudgetException(BudgetErrorCodes.Exhausted, "budget exhausted") };
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await service.InvokeAsync(Request(withBudget: true));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.Exhausted, result.ErrorCode);
        Assert.AreEqual(0, client.Calls, "A rejected attempt must not reach the provider.");
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task ProviderFailure_AfterDispatch_MarksTheAttemptUnknown()
    {
        var client = new FakeClient(new InvalidOperationException("provider down"));
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await service.InvokeAsync(Request(withBudget: true));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task MissingProviderUsage_IsMarkedUnknown_NotSettledAsZero()
    {
        var client = new FakeClient(new LlmResponse("ok", [], null, null, null));
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await service.InvokeAsync(Request(withBudget: true));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task RequestWithoutBudgetIdentity_DoesNotTouchTheGate()
    {
        var client = new FakeClient(Response());
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);

        var result = await service.InvokeAsync(Request(withBudget: false));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, port.Begins);
        Assert.AreEqual(0, port.Settles);
        Assert.AreEqual(1, client.Calls);
    }

    [TestMethod]
    public async Task BudgetedStream_SettlesTheReportedUsage()
    {
        var client = new FakeClient(Response())
        {
            StreamDeltas =
            [
                new StreamDelta { ContentDelta = "ok" },
                new StreamDelta { Usage = new TokenUsageDto { PromptTokens = 300, CompletionTokens = 40, PromptCacheHitTokens = 100 } },
            ],
        };
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        var text = string.Empty;
        await foreach (var delta in service.InvokeStreamAsync(Request(withBudget: true)))
            text += delta.ContentDelta;
        Assert.AreEqual("ok", text);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(300L, port.LastUsage!.InputTokens);
        Assert.AreEqual(40L, port.LastUsage.OutputTokens);
        Assert.AreEqual(100L, port.LastUsage.CacheHitTokens);
    }

    [TestMethod]
    public async Task BudgetedStream_RejectedGateDoesNotStartTheStream()
    {
        var client = new FakeClient(Response());
        var port = new FakeBudgetPort
        {
            BeginFailure = new ModelAttemptBudgetException(BudgetErrorCodes.Exhausted, "budget exhausted"),
        };
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamAsync(Request(withBudget: true))) { }
        });
        Assert.AreEqual(BudgetErrorCodes.Exhausted, failure.Code);
        Assert.AreEqual(0, client.StreamCalls);
    }

    [TestMethod]
    public async Task BudgetedStream_ProviderFailureMarksUnknown()
    {
        var client = new FakeClient(Response()) { StreamFailure = new InvalidOperationException("provider down") };
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamAsync(Request(withBudget: true))) { }
        });
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task BudgetedStream_AbandonedByConsumerMarksUnknown()
    {
        var client = new FakeClient(Response()) { StreamDeltas = [new StreamDelta { ContentDelta = "one" },
            new StreamDelta { ContentDelta = "two" }] };
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        await foreach (var _ in service.InvokeStreamAsync(Request(withBudget: true))) break;
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task BudgetBoundWithoutPort_FailsBeforeProvider_Buffered()
    {
        var client = new FakeClient(Response());
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance);
        var result = await service.InvokeAsync(Request(withBudget: true));
        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, result.ErrorCode);
        Assert.AreEqual(0, client.Calls, "A budget-bound request without the port must not reach the provider.");
    }

    [TestMethod]
    public async Task BudgetBoundWithoutPort_FailsBeforeStreamCreation()
    {
        var client = new FakeClient(Response());
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance);
        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamAsync(Request(withBudget: true))) { }
        });
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, failure.Code);
        Assert.AreEqual(0, client.StreamCalls);
    }

    [TestMethod]
    public async Task BudgetIdentityMismatch_FailsBeforeProvider()
    {
        var client = new FakeClient(Response());
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        var request = Request(withBudget: true);
        request = request with
        {
            BudgetAttempt = request.BudgetAttempt! with { WorkspaceId = "other-workspace" },
        };
        var result = await service.InvokeAsync(request);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, result.ErrorCode);
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task SettlementFailure_MarksTheAttemptUnknown()
    {
        var client = new FakeClient(Response());
        var port = new FakeBudgetPort
        {
            SettleFailure = new ModelAttemptBudgetException(BudgetErrorCodes.SettlementConflict, "receipt lost"),
        };
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        var result = await service.InvokeAsync(Request(withBudget: true));
        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, port.Unknowns);
    }

    [TestMethod]
    public async Task StreamCreationFailure_MarksTheAttemptUnknown()
    {
        var client = new FakeClient(Response()) { ThrowOnStreamCreate = true };
        var port = new FakeBudgetPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamAsync(Request(withBudget: true))) { }
        });
        Assert.AreEqual(1, port.Unknowns);
    }
    private static LlmInvocationRequest Request(bool withBudget) => new()
    {
        WorkspaceId = "ws-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        AgentTemplateId = "template-1",
        Profile = new LlmInvocationProfile { ProviderId = "provider", ProfileId = "profile", ModelId = "model-a" },
        Messages = [new ChatMessage(ChatRole.User, "hello")],
        BudgetAttempt = withBudget
            ? new("account-1", [], "GoalRun", "goal-1", "ws-1", "agent-1", "session-1", "attempt-1", "rev-1",
                "pricing-v1", 1_000, 100, "usd")
            : null,
    };

    private static LlmResponse Response(TokenUsageDto? usage = null) => new("ok", [], null,
        usage ?? new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 }, null);

    private sealed class FakeClient : IRuntimeLlmClient
    {
        private readonly LlmResponse? _response;
        private readonly Exception? _failure;

        public FakeClient(LlmResponse response) => _response = response;
        public FakeClient(Exception failure) => _failure = failure;
        public List<StreamDelta> StreamDeltas { get; init; } = [];
        public bool ThrowOnStreamCreate { get; init; }
        public Exception? StreamFailure { get; init; }
        public int StreamCalls { get; private set; }
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return _failure is not null ? Task.FromException<LlmResponse>(_failure) : Task.FromResult(_response!);
        }

        public IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            CancellationToken ct = default)
        {
            StreamCalls++;
            if (ThrowOnStreamCreate) throw new InvalidOperationException("stream create failed");
            return StreamAsync(ct);
        }

        private async IAsyncEnumerable<StreamDelta> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (StreamFailure is not null)
            {
                await Task.CompletedTask;
                throw StreamFailure;
            }
            foreach (var delta in StreamDeltas)
            {
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield return delta;
            }
        }
    }

    private sealed class FakeBudgetPort : IModelAttemptBudgetPort
    {
        public Exception? BeginFailure { get; init; }
        public Exception? SettleFailure { get; init; }
        public int Begins { get; private set; }
        public int Settles { get; private set; }
        public int Unknowns { get; private set; }
        public ModelUsage? LastUsage { get; private set; }

        public ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
            CancellationToken ct = default)
        {
            Begins++;
            if (BeginFailure is not null) throw BeginFailure;
            return ValueTask.FromResult<ModelAttemptBudgetGrant?>(new("reservation-1", claim.AttemptId, Shadow: false,
                OwnerEpoch: 1, CancellationRevision: 0, InputPerMillion: 1m, CacheHitPerMillion: 0.5m,
                OutputPerMillion: 2m, BaseUnit: 1_000_000, CostUnit: "usd"));
        }

        public ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
            CancellationToken ct = default)
        {
            Settles++;
            LastUsage = usage;
            if (SettleFailure is not null) throw SettleFailure;
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkUnknownAsync(ModelAttemptBudgetGrant grant, string reasonCode,
            CancellationToken ct = default)
        {
            Unknowns++;
            return ValueTask.CompletedTask;
        }
    }
}
