using System.Collections.Immutable;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResourcesTests;

/// <summary>PA-01 01B/01C: the attempt seam must reserve before the provider call, linearize dispatch against
/// cancellation, settle exactly once and keep unknown spend unknown; restart reconciliation must not double charge.</summary>
[TestClass]
public sealed class DurableBudgetCoordinatorTests
{
    private static readonly BudgetIdentity Identity = new(BudgetAccountKind.Work, "work-1", "workspace-1", "agent-1", "session-1");
    private static readonly BudgetAccountId Root = new("root-1");
    private static readonly BudgetAccountId Child = new("child-1");

    [TestMethod]
    public async Task ExecuteAsync_ExhaustedBudget_NeverCallsTheProvider()
    {
        var ledger = NewLedger(maxOutput: 0);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var calls = 0;
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await coordinator.ExecuteAsync<Outcome>(Reserve("op-1", "attempt-1"),
                _ => { calls++; return Task.FromResult(new Outcome(new(1, 1), .001m, "receipt-1")); },
                o => o.Usage, o => o.Cost, o => o.Receipt));
        Assert.AreEqual(BudgetErrorCodes.Exhausted, failure.Code);
        Assert.AreEqual(0, calls, "The provider spy must not be called when the durable budget admits nothing.");
    }

    [TestMethod]
    public async Task Dispatch_AfterCancellation_IsRejectedAtTheAttemptLevel()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var attempt = await coordinator.BeginAsync(Reserve("op-1", "attempt-1"));
        await ledger.CancelAsync(new(Root, "user.cancelled", 1));
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () => await attempt.DispatchAsync());
        Assert.AreEqual(BudgetErrorCodes.Cancelled, failure.Code, "The dispatch linearization point must observe the new cancellation revision.");
        await attempt.DisposeAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_ReservesBeforeDispatch_ThenSettlesOnce()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        BudgetSnapshot? observed = null;
        var outcome = await coordinator.ExecuteAsync<Outcome>(Reserve("op-1", "attempt-1"),
            async _ => { observed = await ledger.ReadAsync(Root); return new Outcome(new(10, 4), .002m, "receipt-1"); },
            o => o.Usage, o => o.Cost, o => o.Receipt);
        Assert.IsNotNull(observed);
        Assert.AreEqual(10L, observed.ReservedOutputTokens, "The reservation must exist before the provider is called.");
        Assert.AreEqual(1, observed.ActiveAttempts);
        var after = await ledger.ReadAsync(Root);
        Assert.AreEqual(4L, after.OutputTokens);
        Assert.AreEqual(0L, after.ReservedOutputTokens);
        Assert.AreEqual(0, after.ActiveAttempts);
        Assert.AreEqual(.002m, after.Cost);
    }

    [TestMethod]
    public async Task ExecuteAsync_ProviderFailure_KeepsUnknownAndRethrows()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await coordinator.ExecuteAsync<Outcome>(Reserve("op-1", "attempt-1"),
                _ => throw new InvalidOperationException("provider exploded"),
                o => o.Usage, o => o.Cost, o => o.Receipt);
        });
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(10L, snapshot.UnknownOutputTokens, "A dispatched attempt without a response keeps its ceiling unknown.");
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancelledBeforeDispatch_RejectsWithoutProviderCall()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        await ledger.CancelAsync(new(Root, "user.cancelled", 1));
        var calls = 0;
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
        {
            await coordinator.ExecuteAsync<Outcome>(Reserve("op-1", "attempt-1"),
                _ => { calls++; return Task.FromResult(new Outcome(new(1, 1), .001m, "receipt-1")); },
                o => o.Usage, o => o.Cost, o => o.Receipt);
        });
        Assert.AreEqual(BudgetErrorCodes.Cancelled, failure.Code);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingUsage_StaysUnknown_NotFree()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
        {
            await coordinator.ExecuteAsync<Outcome>(Reserve("op-1", "attempt-1"),
                _ => Task.FromResult(new Outcome(null, 0m, "receipt-1")),
                o => o.Usage, o => o.Cost, o => o.Receipt);
        });
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, failure.Code);
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(10L, snapshot.UnknownOutputTokens);
        var next = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            { await coordinator.BeginAsync(Reserve("op-2", "attempt-2")); });
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, next.Code, "Unknown spend must not admit a new attempt.");
    }

    [TestMethod]
    public async Task RetryAfterReleasedAttempt_ReusesOperationId_ChargesOnce()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        await using (var abandoned = await coordinator.BeginAsync(Reserve("op-retry", "attempt-1")))
        {
            await abandoned.ReleaseAsync();
        }
        Assert.AreEqual(0L, (await ledger.ReadAsync(Root)).ReservedOutputTokens, "A released attempt refunds its reservation.");

        await coordinator.ExecuteAsync<Outcome>(Reserve("op-retry", "attempt-2"),
            _ => Task.FromResult(new Outcome(new(5, 3), .001m, "receipt-2")),
            o => o.Usage, o => o.Cost, o => o.Receipt);
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(3L, snapshot.OutputTokens);
        Assert.AreEqual(.001m, snapshot.Cost);
    }

    [TestMethod]
    public async Task UnknownAttempt_IsNotRetriedAutomaticallyUnderTheSameOperation()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var attempt = await coordinator.BeginAsync(Reserve("op-1", "attempt-1"));
        await attempt.DispatchAsync();
        await attempt.MarkUnknownAsync("provider.timeout");
        await attempt.DisposeAsync();

        var conflict = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            { await coordinator.BeginAsync(Reserve("op-1", "attempt-2")); });
        Assert.AreEqual(BudgetErrorCodes.Conflict, conflict.Code, "Unknown effects are not retried automatically.");
    }

    [TestMethod]
    public async Task AbandonedDispatchedHandle_KeepsUnknownCeiling()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var attempt = await coordinator.BeginAsync(Reserve("op-1", "attempt-1"));
        await attempt.DispatchAsync();
        await attempt.DisposeAsync();
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(10L, snapshot.UnknownOutputTokens);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
    }

    [TestMethod]
    public async Task Recovery_RefundsPending_KeepsDispatchedUnknown_AndDoesNotResetRoot()
    {
        var ledger = NewLedger(maxOutput: 100);
        var first = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var pending = await ledger.ReserveAsync(Reserve("op-pending", "attempt-pending"));
        var running = await first.BeginAsync(Reserve("op-running", "attempt-running"));
        await running.DispatchAsync();

        var successor = new DurableBudgetCoordinator(ledger, ownerEpoch: 2);
        var actions = await successor.RecoverAsync(Root);
        Assert.AreEqual(2, actions.Length);
        Assert.AreEqual(BudgetRecoveryActionKind.ReleaseUndispatched, actions[0].Kind);
        Assert.AreEqual(pending.ReservationId, actions[0].ReservationId);
        Assert.AreEqual(BudgetRecoveryActionKind.KeepUnknown, actions[1].Kind);

        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens, "The undispatched reservation is refunded.");
        Assert.AreEqual(10L, snapshot.UnknownOutputTokens, "The dispatched attempt stays unknown; a TTL is not proof.");
        Assert.AreEqual(1, snapshot.ActiveAttempts);
        Assert.AreEqual(0L, snapshot.OutputTokens, "Recovery must not invent usage.");
    }

    [TestMethod]
    public async Task Recovery_LateSettlementAfterUnknown_SettlesOnceWithoutDoubleCharge()
    {
        var ledger = NewLedger(maxOutput: 100);
        var first = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        var running = await first.BeginAsync(Reserve("op-1", "attempt-1"));
        await running.DispatchAsync();
        var second = new DurableBudgetCoordinator(ledger, ownerEpoch: 2);
        await second.RecoverAsync(Root);
        var candidate = (await ledger.ListUnsettledAsync(Root)).Single(c => c.AttemptId == "attempt-1");
        Assert.AreEqual(BudgetAttemptPhase.Unknown, candidate.Phase);

        var commands = new SettleBudget(candidate.ReservationId, candidate.AttemptId, 2, new(5, 5), .002m, "usd",
            "late-receipt", DateTimeOffset.UtcNow);
        var settled = await ledger.SettleAsync(commands);
        Assert.IsTrue(settled.FromUnknown);
        var replay = await ledger.SettleAsync(commands);
        Assert.AreEqual(settled.Revision, replay.Revision, "The late receipt is settled exactly once.");
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(5L, snapshot.OutputTokens);
        Assert.AreEqual(0L, snapshot.UnknownOutputTokens);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
    }

    [TestMethod]
    public async Task BufferedAndStreamingChannelsShareTheSameSeam()
    {
        var ledger = NewLedger(maxOutput: 100);
        var coordinator = new DurableBudgetCoordinator(ledger, ownerEpoch: 1);
        async Task<long> RunOnce(string operationId, string attemptId)
        {
            await coordinator.ExecuteAsync<Outcome>(Reserve(operationId, attemptId),
                _ => Task.FromResult(new Outcome(new(5, 5), .001m, $"receipt-{attemptId}")),
                o => o.Usage, o => o.Cost, o => o.Receipt);
            return (await ledger.ReadAsync(Root)).OutputTokens;
        }
        Assert.AreEqual(5L, await RunOnce("op-buffered", "attempt-buffered"));
        Assert.AreEqual(10L, await RunOnce("op-streaming", "attempt-streaming"), "Both channels reserve and settle identically.");
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(2, snapshot.Calls);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
    }

    private static InMemoryExecutionBudgetLedger NewLedger(long? maxOutput = 100)
    {
        var ledger = new InMemoryExecutionBudgetLedger();
        ledger.CreateAccount(Root, null, new BudgetLimits(MaxOutputTokens: maxOutput, MaxConcurrentAttempts: 4), Identity, "usd");
        ledger.CreateAccount(Child, Root, new(), Identity, "usd");
        return ledger;
    }

    private static ReserveBudget Reserve(string operationId, string attemptId, long maxOutput = 10)
        => new(operationId, attemptId, Child, ImmutableArray.Create(Root, Child), Identity,
            "provider/model", "model-v1", "price-v1", 100, maxOutput, .1m, "usd", DateTimeOffset.UtcNow.AddMinutes(2));

    private sealed record Outcome(ModelUsage? Usage, decimal Cost, string Receipt);
}
