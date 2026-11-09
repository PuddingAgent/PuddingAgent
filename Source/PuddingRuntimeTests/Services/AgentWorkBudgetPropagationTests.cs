using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// CORE-I1-05 W4/W5 at the Runtime boundary: the host-bound Work budget carrier is converted into a distinct per-send
/// attempt identity, a managed execution never falls back to the direct client, and the real invocation facade gates the
/// provider call through the budget port with the frozen account/revision/price identity.
/// </summary>
[TestClass]
public sealed class AgentWorkBudgetPropagationTests
{
    [TestMethod]
    public void MappedIdentity_BecomesADistinctPerSendAttempt()
    {
        Assert.IsNull(WorkBudgetAttempt.Create(null));
        Assert.IsNull(WorkBudgetAttempt.Create(new RuntimeExecutionIdentity
        {
            Kind = RuntimeExecutionKind.ConversationTurn, ConversationId = "session-1", RunId = "run-1", TraceId = null,
        }), "An execution without a managed carrier must stay unmanaged (legacy behavior).");

        var first = WorkBudgetAttempt.Create(Identity(Binding()))!;
        var second = WorkBudgetAttempt.Create(Identity(Binding()))!;
        Assert.AreEqual("work:work-1", first.AccountId);
        Assert.AreEqual("Work", first.RootKind);
        Assert.AreEqual("work-1", first.RootId);
        Assert.AreEqual("usd", first.CostUnit);
        Assert.AreEqual("rev-1", first.ModelRevision);
        Assert.AreEqual("pr-1", first.PriceVersion);
        Assert.AreEqual(128_000, first.MaxInputTokens);
        Assert.AreEqual(8_192, first.MaxOutputTokens);
        Assert.AreEqual(0, first.Ancestors.Count);
        Assert.IsTrue(first.IsValid);
        Assert.AreNotEqual(first.AttemptId, second.AttemptId,
            "Every model send must get its own attempt identity over the same Work account.");
        Assert.AreEqual(first.AccountId, second.AccountId);
    }

    [TestMethod]
    public void ManagedExecution_RequiresTheFacadeAndAMatchingInvocationIdentity()
    {
        WorkBudgetAttempt.RequireInvocationFacade(Identity(Binding()), new object());

        var missingFacade = Assert.ThrowsExactly<ModelAttemptBudgetException>(() =>
            WorkBudgetAttempt.RequireInvocationFacade(Identity(Binding()), null));
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, missingFacade.Code);

        WorkBudgetAttempt.RequireInvocationFacade(Identity(null), null);

        var mismatch = Assert.ThrowsExactly<ModelAttemptBudgetException>(() =>
            WorkBudgetAttempt.EnsureIdentityMatches(Identity(Binding()), "ws-2", "agent-1", "session-1"));
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, mismatch.Code);
        var agentMismatch = Assert.ThrowsExactly<ModelAttemptBudgetException>(() =>
            WorkBudgetAttempt.EnsureIdentityMatches(Identity(Binding()), "ws-1", "agent-2", "session-1"));
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, agentMismatch.Code);
    }

    [TestMethod]
    public async Task ManagedAttempt_ReachesTheFacadeOnceAndSettlesWithTheFrozenIdentity()
    {
        var client = new CountingClient();
        var port = new CapturingPort();
        var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port);
        var attempt = WorkBudgetAttempt.Create(Identity(Binding()), "attempt-1")!;

        var result = await service.InvokeAsync(Request(attempt));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual(1, port.Begins);
        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
        Assert.IsNotNull(port.LastClaim);
        Assert.AreEqual("work:work-1", port.LastClaim!.AccountId);
        Assert.AreEqual("Work", port.LastClaim.Identity.RootKind.ToString());
        Assert.AreEqual("work-1", port.LastClaim.Identity.RootId);
        Assert.AreEqual("attempt-1", port.LastClaim.AttemptId);
        Assert.AreEqual("model-a", port.LastClaim.ModelId);
        Assert.AreEqual("rev-1", port.LastClaim.ModelRevision);
        Assert.AreEqual("pr-1", port.LastClaim.PriceVersion);
        Assert.AreEqual(128_000, port.LastClaim.MaxInputTokens);
        Assert.AreEqual(8_192, port.LastClaim.MaxOutputTokens);
        Assert.AreEqual("usd", port.LastClaim.CostUnit);

        // A carrier that does not match the invocation identity is rejected before the provider call.
        var mismatch = await service.InvokeAsync(Request(attempt) with { AgentInstanceId = "agent-2" });
        Assert.IsFalse(mismatch.Success);
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, mismatch.ErrorCode);
        Assert.AreEqual(1, client.Calls, "A mismatched managed identity must not reach the provider.");
        Assert.AreEqual(1, port.Begins);
    }

    private static RuntimeWorkBudgetBinding Binding() => new(
        "work:work-1", [], "Work", "work-1", "ws-1", "agent-1", "session-1", "usd", "rev-1", "pr-1", 128_000, 8_192);

    private static RuntimeExecutionIdentity Identity(RuntimeWorkBudgetBinding? binding) => new()
    {
        Kind = RuntimeExecutionKind.ConversationTurn,
        ConversationId = "session-1",
        RunId = "run-1",
        TraceId = null,
        WorkBudget = binding,
    };

    private static LlmInvocationRequest Request(LlmAttemptBudget attempt) => new()
    {
        WorkspaceId = "ws-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        AgentTemplateId = "template-1",
        Profile = new LlmInvocationProfile { ProviderId = "provider", ProfileId = "profile", ModelId = "model-a" },
        Messages = [new ChatMessage(ChatRole.User, "hello")],
        BudgetAttempt = attempt,
    };

    private sealed class CountingClient : IRuntimeLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse("ok", [], null,
                new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 }, null));
        }

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingPort : IModelAttemptBudgetPort
    {
        public int Begins { get; private set; }
        public int Settles { get; private set; }
        public int Unknowns { get; private set; }
        public ModelAttemptBudgetClaim? LastClaim { get; private set; }

        public ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
            CancellationToken ct = default)
        {
            Begins++;
            LastClaim = claim;
            return ValueTask.FromResult<ModelAttemptBudgetGrant?>(new("reservation-1", claim.AttemptId, Shadow: false,
                OwnerEpoch: 1, CancellationRevision: 0, InputPerMillion: 1m, CacheHitPerMillion: 0.5m,
                OutputPerMillion: 2m, BaseUnit: 1_000_000, CostUnit: "usd"));
        }

        public ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
            CancellationToken ct = default)
        {
            Settles++;
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
