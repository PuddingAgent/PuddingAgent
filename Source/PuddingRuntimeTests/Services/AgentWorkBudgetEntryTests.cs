using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;
using PuddingRuntimeTests.Services.TaskE2E;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// CORE-I1-05 W4/W5 at the real runtime entry: the actual buffered (<see cref="AgentExecutionService.ExecuteAsync"/>)
/// and streaming (<see cref="AgentExecutionService.ExecuteStreamAsync"/>) paths are driven with a managed Work carrier,
/// and both must hand a per-send <see cref="LlmAttemptBudget"/> to the real invocation facade. A managed execution
/// without the facade must fail before the direct LLM client (W5), with zero provider calls.
/// </summary>
[TestClass]
public sealed class AgentWorkBudgetEntryTests
{
    private const string Workspace = "ws-1";
    private const string Agent = "agent-1";

    [TestMethod]
    public async Task BufferedAndStreamingEntries_BothCarryThePerSendBudgetAttempt()
    {
        var client = new CountingInvocationClient();
        var port = new CapturingBudgetPort();
        var facade = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        using var harness = new TaskE2EHarness(llmInvocationService: facade);
        await harness.InitializeAsync();

        var buffered = harness.CreateDispatchRequest("session-w4-buffered", null) with
        {
            LlmProfile = Profile(),
            ExecutionIdentity = Identity("session-w4-buffered"),
        };
        var bufferedResult = await harness.ExecutionService.ExecuteAsync(buffered);
        Assert.AreEqual(AgentExecutionState.Completed, bufferedResult.ExecutionState, bufferedResult.ErrorMessage);
        Assert.AreEqual(1, port.Begins, "The buffered entry must gate its provider send through the budget facade.");
        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual("work:" + WorkId, port.Claims[0].AccountId);
        Assert.AreEqual("Work", port.Claims[0].Identity.RootKind.ToString());
        Assert.AreEqual(WorkId, port.Claims[0].Identity.RootId);
        Assert.AreEqual("rev-1", port.Claims[0].ModelRevision);
        Assert.AreEqual("pr-1", port.Claims[0].PriceVersion);

        var streaming = harness.CreateDispatchRequest("session-w4-streaming", null) with
        {
            LlmProfile = Profile(),
            ExecutionIdentity = Identity("session-w4-streaming"),
        };
        await foreach (var _ in harness.ExecutionService.ExecuteStreamAsync(streaming)) { }

        Assert.AreEqual(2, port.Begins, "The streaming entry must also gate its provider send through the facade.");
        Assert.AreEqual(2, client.Calls);
        Assert.AreNotEqual(port.Claims[0].AttemptId, port.Claims[1].AttemptId,
            "Every model send gets its own attempt identity over the same Work account.");
        Assert.AreEqual(port.Claims[0].AccountId, port.Claims[1].AccountId);
    }

    [TestMethod]
    public async Task ManagedExecution_WithoutFacade_FailsBeforeTheDirectClient()
    {
        using var harness = new TaskE2EHarness();
        await harness.InitializeAsync();
        var request = harness.CreateDispatchRequest("session-w5", null) with
        {
            LlmProfile = Profile(),
            ExecutionIdentity = Identity("session-w5"),
        };

        var result = await harness.ExecutionService.ExecuteAsync(request);

        Assert.AreEqual(AgentExecutionState.Failed, result.ExecutionState);
        Assert.AreEqual(0, harness.Llm.CallCount,
            "A managed execution must never fall back to the direct LLM client when the facade is missing.");
        Assert.IsTrue(result.ErrorMessage?.Contains("dispatch_invalid", StringComparison.OrdinalIgnoreCase) == true
                      || result.ErrorMessage?.Contains("facade", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected a typed fail-closed error, got: {result.ErrorMessage}");
    }

    [TestMethod]
    public void ManagedCarrier_SerializesOnlyHostBoundReferences()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Identity("session-w5"));
        Assert.IsTrue(json.Contains("work:" + WorkId, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
        // The identity is the only carrier: a runtime request without it has no budget authority at all.
        Assert.IsNull(new RuntimeExecutionIdentity
        {
            Kind = RuntimeExecutionKind.ConversationTurn, ConversationId = "c", RunId = "r", TraceId = null,
        }.WorkBudget);
    }

    private const string AgentReply = "{\"status\":\"DONE\",\"message\":\"finished\"}";

    private const string WorkId = "work-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RuntimeExecutionIdentity Identity(string sessionId) => new()
    {
        Kind = RuntimeExecutionKind.ConversationTurn,
        ConversationId = sessionId,
        RunId = "run-1",
        TraceId = null,
        WorkBudget = new RuntimeWorkBudgetBinding(
            "work:" + WorkId, [], "Work", WorkId, Workspace, Agent, sessionId, "usd", "rev-1", "pr-1",
            128_000, 8_192),
    };

    private static LlmInvocationProfile Profile() => new()
    {
        ProviderId = "provider-a", ProfileId = "profile-a", ModelId = "model-a", Role = "conscious",
    };

    private sealed class CountingInvocationClient : IRuntimeLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse(AgentReply, [], null,
                new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 }, null));
        }

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new StreamDelta
            {
                ContentDelta = AgentReply,
                Usage = new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 },
                FinishReason = "stop",
            };
        }
    }

    private sealed class CapturingBudgetPort : IModelAttemptBudgetPort
    {
        public List<ModelAttemptBudgetClaim> Claims { get; } = [];
        public int Begins => Claims.Count;
        public int Settles { get; private set; }

        public ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
            CancellationToken ct = default)
        {
            Claims.Add(claim);
            return ValueTask.FromResult<ModelAttemptBudgetGrant?>(new("reservation-1", claim.AttemptId,
                Shadow: false, OwnerEpoch: 1, CancellationRevision: 0, InputPerMillion: 1m,
                CacheHitPerMillion: 0.5m, OutputPerMillion: 2m, BaseUnit: 1_000_000, CostUnit: "usd"));
        }

        public ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
            CancellationToken ct = default)
        {
            Settles++;
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkUnknownAsync(ModelAttemptBudgetGrant grant, string reasonCode,
            CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
