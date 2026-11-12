using System.Collections.Immutable;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03/PA-01 sequencer on the real in-memory ledger: reserve+dispatch, settle with usage, dispatch failure refunds
/// only before the linearization point, abandon keeps unknown amounts (no refund), and stale owners cannot settle.
/// </summary>
[TestClass]
public sealed class ReviewBudgetSequencerTests
{
    [TestMethod]
    public async Task BeginReservesAndDispatches_ThenSettleRecordsUsage()
    {
        var ledger = Ledger(out var account);
        var sequencer = new ReviewBudgetSequencer(ledger);
        var budget = await sequencer.BeginAsync(Command(account, "attempt-1"), ownerEpoch: 3);

        Assert.AreEqual(3L, budget.OwnerEpoch);
        var active = await ledger.ReadAsync(account);
        Assert.AreEqual(1, active.ActiveAttempts);
        Assert.AreEqual(400L, active.ReservedOutputTokens);

        var receipt = await sequencer.SettleAsync(budget, new ModelUsage(10, 20, 0), 0.2m, "usd", "receipt-1",
            DateTimeOffset.UtcNow);
        Assert.IsFalse(receipt.Overrun);
        var settled = await ledger.ReadAsync(account);
        Assert.AreEqual(20L, settled.OutputTokens);
        Assert.AreEqual(0.2m, settled.Cost);
        Assert.AreEqual(0, settled.ActiveAttempts);
        Assert.AreEqual(0L, settled.ReservedOutputTokens);
    }

    [TestMethod]
    public async Task DispatchFailure_RefundsOnlyThePendingReservation()
    {
        var ledger = Ledger(out var account);
        await ledger.CancelAsync(new(account, "user.cancelled", 1));
        var sequencer = new ReviewBudgetSequencer(ledger);

        await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await sequencer.BeginAsync(Command(account, "attempt-cancelled"), ownerEpoch: 1));
        var snapshot = await ledger.ReadAsync(account);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.IsTrue(snapshot.Cancelled);
    }

    [TestMethod]
    public async Task DispatchFailureAfterReserve_ReleasesTheReservation()
    {
        var inner = Ledger(out var account);
        var ledger = new FailingDispatchLedger(inner);
        var sequencer = new ReviewBudgetSequencer(ledger);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await sequencer.BeginAsync(Command(account, "attempt-dispatch-fail"), ownerEpoch: 1));
        Assert.IsTrue(ledger.Released, "A reservation that never dispatched must be released.");
        var snapshot = await inner.ReadAsync(account);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
    }
    [TestMethod]
    public async Task AbandonKeepsTheAmountsUnknown_AndNeverRefunds()
    {
        var ledger = Ledger(out var account);
        var sequencer = new ReviewBudgetSequencer(ledger);
        var budget = await sequencer.BeginAsync(Command(account, "attempt-unknown"), ownerEpoch: 2);

        var snapshot = await sequencer.AbandonAsync(budget, "provider.timeout");
        Assert.AreEqual(400L, snapshot.UnknownOutputTokens);
        Assert.AreEqual(1m, snapshot.UnknownCost);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task StaleOwnerCannotSettle_AndInvalidAbandonReasonIsTyped()
    {
        var ledger = Ledger(out var account);
        var sequencer = new ReviewBudgetSequencer(ledger);
        var budget = await sequencer.BeginAsync(Command(account, "attempt-1"), ownerEpoch: 5);
        var stale = budget with { Permit = budget.Permit with { OwnerEpoch = 1 } };

        await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await sequencer.SettleAsync(stale, new ModelUsage(1, 1, 0), 0.01m, "usd", "r", DateTimeOffset.UtcNow));
        var invalid = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await sequencer.AbandonAsync(budget, " "));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, invalid.Code);
    }

    private static InMemoryExecutionBudgetLedger Ledger(out BudgetAccountId account)
    {
        var ledger = new InMemoryExecutionBudgetLedger();
        account = new("review-budget-1");
        ledger.CreateAccount(account, null, new(MaxOutputTokens: 100_000, MaxCost: 100m, MaxCalls: 100,
            MaxConcurrentAttempts: 4), new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd");
        return ledger;
    }

    private static ReserveBudget Command(BudgetAccountId account, string attemptId) => new($"op-{attemptId}", attemptId,
        account, [account], new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "model-a",
        "rev-1", "price-1", 1000, 400, 1m, "usd", DateTimeOffset.UtcNow.AddMinutes(5));
    private sealed class FailingDispatchLedger(IExecutionBudgetLedger inner) : IExecutionBudgetLedger
    {
        public bool Released { get; private set; }

        public ValueTask<BudgetReservation> ReserveAsync(ReserveBudget command, CancellationToken ct = default)
            => inner.ReserveAsync(command, ct);

        public ValueTask<DispatchPermit> MarkDispatchedAsync(DispatchBudget command, CancellationToken ct = default)
            => throw new InvalidOperationException("dispatch failed");

        public ValueTask<SettlementReceipt> SettleAsync(SettleBudget command, CancellationToken ct = default)
            => inner.SettleAsync(command, ct);

        public ValueTask<BudgetSnapshot> MarkUnknownAsync(MarkUnknownBudget command, CancellationToken ct = default)
            => inner.MarkUnknownAsync(command, ct);

        public ValueTask<BudgetSnapshot> ReleaseAsync(ReleaseBudget command, CancellationToken ct = default)
        {
            Released = true;
            return inner.ReleaseAsync(command, ct);
        }

        public ValueTask<CancelBudgetReceipt> CancelAsync(CancelBudget command, CancellationToken ct = default)
            => inner.CancelAsync(command, ct);

        public ValueTask<BudgetSnapshot> ReadAsync(BudgetAccountId account, CancellationToken ct = default)
            => inner.ReadAsync(account, ct);
    }
}
