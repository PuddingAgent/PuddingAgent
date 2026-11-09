using PuddingCode.Observability;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// CORE-I1-06 slice: one real send = one budget attempt. The observer is the only budget writer per send; it must issue a
/// fresh attempt id per send, keep the frozen Work identity/revision/price, clamp the reserved output, settle actual
/// usage once, and never convert unknown usage into zero.
/// </summary>
[TestClass]
public sealed class BudgetedSendAttemptObserverTests
{
    [TestMethod]
    public async Task EverySendGetsItsOwnAttemptWithTheFrozenWorkIdentity()
    {
        var port = new CapturingPort();
        var observer = new BudgetedSendAttemptObserver(port, Binding(), clock: () => FixedNow);

        var first = await observer.BeforeSendAsync(Request(maxOutput: 4_096));
        var second = await observer.BeforeSendAsync(Request(maxOutput: 8_192));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsTrue(first!.Gated);
        Assert.AreNotEqual(first.AttemptId, second!.AttemptId);
        Assert.AreEqual(2, port.Begins);
        Assert.AreEqual("work:work-1", port.Claims[0].AccountId);
        Assert.AreEqual("Work", port.Claims[0].Identity.RootKind.ToString());
        Assert.AreEqual("work-1", port.Claims[0].Identity.RootId);
        Assert.AreEqual("rev-1", port.Claims[0].ModelRevision);
        Assert.AreEqual("pr-1", port.Claims[0].PriceVersion);
        Assert.AreEqual("model-a", port.Claims[0].ModelId);
        Assert.AreEqual(128_000, port.Claims[0].MaxInputTokens);
        Assert.AreEqual(4_096, port.Claims[0].MaxOutputTokens, "A smaller request bound must tighten the attempt.");
        Assert.AreEqual(8_192, port.Claims[1].MaxOutputTokens);
        Assert.AreEqual(first.AttemptId, port.Claims[0].AttemptId);
        Assert.AreEqual(FixedNow, port.Claims[0].DispatchAt);
    }

    [TestMethod]
    public async Task CompletionSettlesActualUsageExactlyOnce()
    {
        var port = new CapturingPort();
        var observer = new BudgetedSendAttemptObserver(port, Binding(), clock: () => FixedNow);
        var permit = await observer.BeforeSendAsync(Request(8_192));

        await observer.CompleteAsync(permit, new LlmSendUsage(1_000, 200, 400), "receipt-1", failureReason: null);
        await observer.CompleteAsync(permit, new LlmSendUsage(9_999, 9_999), "receipt-2", failureReason: null);

        Assert.AreEqual(1, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
        Assert.AreEqual(1_000, port.LastUsage!.InputTokens);
        Assert.AreEqual(200, port.LastUsage.OutputTokens);
        Assert.AreEqual(400, port.LastUsage.CacheHitTokens);
    }

    [TestMethod]
    public async Task MissingUsageOrFailure_StaysUnknownNeverZero()
    {
        var port = new CapturingPort();
        var observer = new BudgetedSendAttemptObserver(port, Binding(), clock: () => FixedNow);

        var noUsage = await observer.BeforeSendAsync(Request(8_192));
        await observer.CompleteAsync(noUsage, usage: null, providerReceipt: null, failureReason: null);
        Assert.AreEqual(1, port.Unknowns);
        Assert.AreEqual(0, port.Settles);
        Assert.AreEqual("llm.usage_unknown", port.LastUnknownReason);

        var failed = await observer.BeforeSendAsync(Request(8_192));
        await observer.CompleteAsync(failed, usage: new LlmSendUsage(10, 1), providerReceipt: null,
            failureReason: "provider.failed");
        Assert.AreEqual(2, port.Unknowns);
        Assert.AreEqual("provider.failed", port.LastUnknownReason);
        Assert.AreEqual(0, port.Settles);
    }

    [TestMethod]
    public async Task LegacyRootReturnsUngatedPermitWithoutLedgerWrites()
    {
        var port = new CapturingPort { BeginReturnsNull = true };
        var observer = new BudgetedSendAttemptObserver(port, Binding(), clock: () => FixedNow);

        var permit = await observer.BeforeSendAsync(Request(8_192));
        await observer.CompleteAsync(permit, new LlmSendUsage(10, 1), "receipt", failureReason: null);

        Assert.IsNotNull(permit);
        Assert.IsFalse(permit!.Gated);
        Assert.AreEqual(0, port.Settles);
        Assert.AreEqual(0, port.Unknowns);
    }

    [TestMethod]
    public async Task InvalidBudgetIdentity_IsRejectedBeforeThePort()
    {
        var port = new CapturingPort();
        var invalid = Binding() with { AccountId = "" };
        var observer = new BudgetedSendAttemptObserver(port, invalid, clock: () => FixedNow);

        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
            await observer.BeforeSendAsync(Request(8_192)));
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, failure.Code);
        Assert.AreEqual(0, port.Begins);
    }

    [TestMethod]
    public async Task ScopeBindsTheObserverToTheCurrentLogicalInvocation()
    {
        var port = new CapturingPort();
        var observer = new BudgetedSendAttemptObserver(port, Binding(), clock: () => FixedNow);
        Assert.IsNull(LlmSendAttemptScope.Current);

        using (LlmSendAttemptScope.Push(observer))
        {
            Assert.AreSame(observer, LlmSendAttemptScope.Current);
            var permit = await LlmSendAttemptScope.BeforeSendAsync(Request(8_192));
            Assert.IsNotNull(permit);
        }

        Assert.IsNull(LlmSendAttemptScope.Current);
        var unmanaged = await LlmSendAttemptScope.BeforeSendAsync(Request(8_192));
        Assert.IsNull(unmanaged);
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 11, 8, 2, 0, 0, TimeSpan.Zero);

    private static LlmAttemptBudget Binding() => new(
        "work:work-1", [], "Work", "work-1", "ws-1", "agent-1", "session-1", "attempt-base", "rev-1", "pr-1",
        128_000, 8_192, "usd");

    private static LlmSendAttemptRequest Request(long maxOutput) => new(
        "model-a", "rev-1", "pr-1", "usd", 128_000, maxOutput, FixedNow);

    private sealed class CapturingPort : IModelAttemptBudgetPort
    {
        public bool BeginReturnsNull { get; init; }
        public int Begins { get; private set; }
        public int Settles { get; private set; }
        public int Unknowns { get; private set; }
        public List<ModelAttemptBudgetClaim> Claims { get; } = [];
        public ModelUsage? LastUsage { get; private set; }
        public string? LastUnknownReason { get; private set; }

        public ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
            CancellationToken ct = default)
        {
            Begins++;
            Claims.Add(claim);
            return ValueTask.FromResult<ModelAttemptBudgetGrant?>(BeginReturnsNull
                ? null
                : new ModelAttemptBudgetGrant("reservation-1", claim.AttemptId, Shadow: false, OwnerEpoch: 1,
                    CancellationRevision: 0, InputPerMillion: 1m, CacheHitPerMillion: 0.5m, OutputPerMillion: 2m,
                    BaseUnit: 1_000_000, CostUnit: claim.CostUnit));
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
            Unknowns++;
            LastUnknownReason = reasonCode;
            return ValueTask.CompletedTask;
        }
    }
}
