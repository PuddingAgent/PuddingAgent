using System.Collections.Immutable;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResourcesTests;

/// <summary>PA-01 01A: the durable budget ports must hold cross-round and ancestor-atomic invariants on the in-memory ledger.</summary>
[TestClass]
public sealed class DurableBudgetTests
{
    private static readonly BudgetIdentity Identity = new(BudgetAccountKind.Work, "work-1", "workspace-1", "agent-1", "session-1");
    private static readonly BudgetAccountId Root = new("root-1");
    private static readonly BudgetAccountId Child = new("child-1");

    [TestMethod]
    public async Task Reserve_ChecksAllAncestorsAtomically_OnlyOneSiblingWins()
    {
        var ledger = NewLedger(maxOutput: 10);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await ledger.ReserveAsync(Reserve(i % 2 == 0 ? Child : new("child-2"), i % 2 == 0 ? [Root, Child] : [Root, new("child-2")], $"op-{i}", $"attempt-{i}"));
                return "reserved";
            }
            catch (ExecutionResourceException ex) { return ex.Code; }
        })));
        Assert.AreEqual(1, results.Count(r => r == "reserved"), "The root ceiling must admit exactly one of the racing siblings.");
        Assert.AreEqual(7, results.Count(r => r == BudgetErrorCodes.Exhausted));
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(10L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(1, snapshot.ActiveAttempts);
    }

    [TestMethod]
    public async Task Reserve_RejectsForgedOrTruncatedAncestorPath()
    {
        var ledger = NewLedger();
        var withoutRoot = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Child], "op-1", "attempt-1")));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, withoutRoot.Code);
        var extraAncestor = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, new("child-2"), Child], "op-2", "attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, extraAncestor.Code);
        var empty = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, default, "op-3", "attempt-3")));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, empty.Code);
    }

    [TestMethod]
    public async Task Reserve_SameOperationIsIdempotent_ButDifferentInputConflicts()
    {
        var ledger = NewLedger();
        var first = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        var replay = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        Assert.AreEqual(first.ReservationId, replay.ReservationId);
        Assert.AreEqual(1, (await ledger.ReadAsync(Root)).ActiveAttempts, "A replay must not reserve twice.");

        var againWithOtherAttempt = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-9")));
        Assert.AreEqual(BudgetErrorCodes.Conflict, againWithOtherAttempt.Code);
        var otherBounds = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1", maxOutput: 11)));
        Assert.AreEqual(BudgetErrorCodes.Conflict, otherBounds.Code);
    }

    [TestMethod]
    public async Task DispatchAndReserve_AfterCancel_AreRejected()
    {
        var ledger = NewLedger();
        var running = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        await ledger.MarkDispatchedAsync(new(running.ReservationId, "attempt-1", 1, 0));
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-2", "attempt-2"));
        var cancelled = await ledger.CancelAsync(new(Root, "user.cancelled", 1));
        Assert.IsFalse(cancelled.AlreadyCancelled);
        Assert.AreEqual(1, cancelled.RunningAttempts, "Only the dispatched attempt is still running; a pending reservation is not.");

        var dispatch = await FailsAsync(() => ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-2", 1, cancelled.CancellationRevision)));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, dispatch.Code, "Cancellation recorded before dispatch must win the linearization point.");
        var afterCancel = await FailsAsync(() => ledger.ReserveAsync(Reserve(new("child-2"), [Root, new("child-2")], "op-3", "attempt-3")));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, afterCancel.Code, "A cancelled root admits no child attempt.");
        var secondCancel = await ledger.CancelAsync(new(Root, "user.cancelled", 1));
        Assert.IsTrue(secondCancel.AlreadyCancelled);
    }

    [TestMethod]
    public async Task Dispatch_StaleCancellationRevision_Conflicts()
    {
        var ledger = NewLedger();
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        var stale = await FailsAsync(() => ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, ExpectedCancellationRevision: 7)));
        Assert.AreEqual(BudgetErrorCodes.Conflict, stale.Code);
    }

    [TestMethod]
    public async Task Settle_RequiresDispatch_AndIsOnceWithMatchingReceipt()
    {
        var ledger = NewLedger();
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        var early = await FailsAsync(() => ledger.SettleAsync(Settle(pending, 1, new(10, 5), .02m, "receipt-1")));
        Assert.AreEqual(BudgetErrorCodes.SettlementInvalid, early.Code);

        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        var settled = await ledger.SettleAsync(Settle(pending, 1, new(10, 5), .02m, "receipt-1"));
        Assert.AreEqual(5L, settled.Usage.OutputTokens);
        Assert.IsFalse(settled.Overrun);

        var replay = await ledger.SettleAsync(Settle(pending, 1, new(10, 5), .02m, "receipt-1"));
        Assert.AreEqual(settled.Revision, replay.Revision, "The same receipt must be idempotent.");
        var conflict = await FailsAsync(() => ledger.SettleAsync(Settle(pending, 1, new(10, 6), .02m, "receipt-1")));
        Assert.AreEqual(BudgetErrorCodes.SettlementConflict, conflict.Code);
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(5L, snapshot.OutputTokens);
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
    }

    [TestMethod]
    public async Task Settle_OverrunRecordsActualUsage_AndClosesNewAdmission()
    {
        var ledger = NewLedger(maxOutput: 100);
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1", maxOutput: 10, maxCost: .01m));
        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        var settled = await ledger.SettleAsync(Settle(pending, 1, new(10, 20), .5m, "receipt-overrun"));
        Assert.IsTrue(settled.Overrun, "Actual usage above the frozen reservation must be visible, not truncated.");
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(20L, snapshot.OutputTokens);
        Assert.AreEqual(.5m, snapshot.Cost);
        var next = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-2", "attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.ProviderOverrun, next.Code, "An overrun closes further admission.");
    }

    [TestMethod]
    public async Task UnknownRetainsTheCeiling_ThenLateSettlementConvertsItOnce()
    {
        var ledger = NewLedger(maxOutput: 100);
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1", maxOutput: 30));
        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        var unknown = await ledger.MarkUnknownAsync(new(pending.ReservationId, "attempt-1", 1, "provider.timeout"));
        Assert.AreEqual(30L, unknown.UnknownOutputTokens);
        var ceiling = await ledger.ReadAsync(Root);
        Assert.AreEqual(30L, ceiling.UnknownOutputTokens);
        Assert.AreEqual(0L, ceiling.ReservedOutputTokens);
        Assert.AreEqual(70L, ceiling.RemainingOutputTokens, "Unknown spend still consumes the ceiling.");
        var blocked = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-2", "attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, blocked.Code, "Unknown usage must not look free.");

        var settled = await ledger.SettleAsync(Settle(pending, 1, new(10, 12), .03m, "receipt-late"));
        Assert.IsTrue(settled.FromUnknown);
        var after = await ledger.ReadAsync(Root);
        Assert.AreEqual(0L, after.UnknownOutputTokens);
        Assert.AreEqual(12L, after.OutputTokens);
        Assert.AreEqual(0, after.ActiveAttempts);
        Assert.IsNull(after.ReasonCode, "A reconciled attempt clears the unknown failure.");
        var second = await ledger.SettleAsync(Settle(pending, 1, new(10, 12), .03m, "receipt-late"));
        Assert.AreEqual(settled.Revision, second.Revision, "The late receipt is settled exactly once.");
    }

    [TestMethod]
    public async Task Unknown_RequiresDispatch_AndSettledIsNotUnknown()
    {
        var ledger = NewLedger();
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        var early = await FailsAsync(() => ledger.MarkUnknownAsync(new(pending.ReservationId, "attempt-1", 1, "provider.timeout")));
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, early.Code);

        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        await ledger.SettleAsync(Settle(pending, 1, new(1, 1), .001m, "receipt-1"));
        var late = await FailsAsync(() => ledger.MarkUnknownAsync(new(pending.ReservationId, "attempt-1", 1, "provider.timeout")));
        Assert.AreEqual(BudgetErrorCodes.UnknownStale, late.Code);
    }

    [TestMethod]
    public async Task StaleOwnerCannotSettle_NewerOwnerEpochCan()
    {
        var ledger = NewLedger();
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 2, 0));
        var stale = await FailsAsync(() => ledger.SettleAsync(Settle(pending, 1, new(1, 1), .001m, "receipt-1")));
        Assert.AreEqual(BudgetErrorCodes.OwnerStale, stale.Code);
        var takeover = await ledger.SettleAsync(Settle(pending, 3, new(1, 1), .001m, "receipt-1"));
        Assert.AreEqual(1L, takeover.Usage.OutputTokens);
    }

    [TestMethod]
    public async Task NextRoundContinuesTheSameRoot_RemainingBudgetIsNotReset()
    {
        var ledger = NewLedger(maxOutput: 30);
        var roundOne = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-round-1", "attempt-round-1", maxOutput: 20));
        await ledger.MarkDispatchedAsync(new(roundOne.ReservationId, "attempt-round-1", 1, 0));
        await ledger.SettleAsync(Settle(roundOne, 1, new(5, 20), .02m, "receipt-round-1"));
        var afterFirst = await ledger.ReadAsync(Root);
        Assert.AreEqual(20L, afterFirst.OutputTokens);

        var roundTwo = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-round-2", "attempt-round-2", maxOutput: 10));
        Assert.AreEqual(Root, roundOne.RootAccountId, "A new turn keeps the same root account.");
        Assert.AreEqual(Root, roundTwo.RootAccountId, "A new turn keeps the same root account.");
        Assert.IsTrue(roundTwo.Revision > roundOne.Revision, "The same root account keeps a monotonic revision.");
        await ledger.MarkDispatchedAsync(new(roundTwo.ReservationId, "attempt-round-2", 1, 0));
        await ledger.SettleAsync(Settle(roundTwo, 1, new(2, 4), .01m, "receipt-round-2"));
        var afterSecond = await ledger.ReadAsync(Root);
        Assert.AreEqual(24L, afterSecond.OutputTokens);
        Assert.AreEqual(30L - 24L, afterSecond.RemainingOutputTokens);
        var exhausted = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-round-3", "attempt-round-3", maxOutput: 7)));
        Assert.AreEqual(BudgetErrorCodes.Exhausted, exhausted.Code);
    }

    [TestMethod]
    public async Task InputCapacityIsPerRequest_AndCumulativeInputIsNotAFee()
    {
        var ledger = NewLedger(maxOutput: 1000, maxInputPerRequest: 100);
        for (var round = 1; round <= 2; round++)
        {
            var reservation = await ledger.ReserveAsync(Reserve(Child, [Root, Child], $"op-{round}", $"attempt-{round}", maxInput: 100));
            await ledger.MarkDispatchedAsync(new(reservation.ReservationId, reservation.AttemptId, 1, 0));
            await ledger.SettleAsync(Settle(reservation, 1, new(100, 5), .01m, $"receipt-{round}"));
        }
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(200L, snapshot.InputTokens, "Cumulative input is recorded but never blocks a correctly bounded request.");
        var tooLarge = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-3", "attempt-3", maxInput: 101)));
        Assert.AreEqual(BudgetErrorCodes.InputCapacity, tooLarge.Code);
    }

    [TestMethod]
    public async Task MixedModelsSettleAtTheirOwnReservedPrice_WithoutDoubleChargingTheParent()
    {
        var ledger = NewLedger(maxOutput: 1000, maxCost: 1m);
        var cheap = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-cheap", "attempt-cheap", maxCost: .01m));
        var expensive = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-expensive", "attempt-expensive", maxCost: .50m));
        Assert.AreEqual(.51m, (await ledger.ReadAsync(Root)).ReservedCost, "Both attempts reserve on the same root.");
        await ledger.MarkDispatchedAsync(new(cheap.ReservationId, "attempt-cheap", 1, 0));
        await ledger.MarkDispatchedAsync(new(expensive.ReservationId, "attempt-expensive", 1, 0));
        await ledger.SettleAsync(Settle(cheap, 1, new(100, 10), .004m, "receipt-cheap"));
        await ledger.SettleAsync(Settle(expensive, 1, new(100, 10), .3m, "receipt-expensive"));
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(.304m, snapshot.Cost, "Cost is the sum of actual per-attempt charges, not a parent re-charge.");
        Assert.AreEqual(0L, snapshot.ReservedCost);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
    }

    [TestMethod]
    public async Task Cancel_LowersOnlyOnce_AndKeepsRunningAttemptsVisible()
    {
        var ledger = NewLedger();
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1"));
        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        var cancel = await ledger.CancelAsync(new(Child, "user.cancelled", 1));
        Assert.AreEqual(1, cancel.RunningAttempts);
        var childSnapshot = await ledger.ReadAsync(Child);
        Assert.AreEqual(cancel.CancellationRevision, childSnapshot.CancellationRevision);
        Assert.IsTrue(childSnapshot.CancellationRevision > 0);
        var second = await ledger.CancelAsync(new(Child, "user.cancelled", 1));
        Assert.IsTrue(second.AlreadyCancelled);
        Assert.AreEqual(cancel.CancellationRevision, second.CancellationRevision);
        var settleAfterCancel = await ledger.SettleAsync(Settle(pending, 1, new(3, 2), .001m, "receipt-1"));
        Assert.AreEqual(2L, settleAfterCancel.Usage.OutputTokens, "A dispatched attempt is still accounted after cancellation.");
    }

    [TestMethod]
    public async Task Read_ReturnsUnknownAsUnknown_AndMissingAccountIsTyped()
    {
        var ledger = NewLedger(maxOutput: 100);
        var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1", maxOutput: 40));
        await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-1", 1, 0));
        await ledger.MarkUnknownAsync(new(pending.ReservationId, "attempt-1", 1, "provider.timeout"));
        var snapshot = await ledger.ReadAsync(Root);
        Assert.AreEqual(40L, snapshot.UnknownOutputTokens);
        Assert.AreEqual(60L, snapshot.RemainingOutputTokens);
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, snapshot.ReasonCode);
        var missing = await FailsAsync(() => ledger.ReadAsync(new("missing")));
        Assert.AreEqual(BudgetErrorCodes.AccountMissing, missing.Code);
    }

    [TestMethod]
    public async Task InvalidCommandsAndBounds_AreTyped_WithoutTouchingTheLedger()
    {
        var ledger = NewLedger();
        var pastDeadline = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-1", "attempt-1",
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.AreEqual(BudgetErrorCodes.RequestInvalid, pastDeadline.Code);
        var badUsage = await FailsAsync(async () =>
        {
            var pending = await ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-2", "attempt-2"));
            await ledger.MarkDispatchedAsync(new(pending.ReservationId, "attempt-2", 1, 0));
            await ledger.SettleAsync(Settle(pending, 1, new(5, 5, 6), .01m, "receipt-1"));
        });
        Assert.AreEqual(BudgetErrorCodes.UsageInvalid, badUsage.Code);
        var currency = await FailsAsync(() => ledger.ReserveAsync(Reserve(Child, [Root, Child], "op-3", "attempt-3", costUnit: "eur")));
        Assert.AreEqual(BudgetErrorCodes.CurrencyMismatch, currency.Code);
        var zeroConcurrency = new InMemoryExecutionBudgetLedger();
        Assert.AreEqual(BudgetErrorCodes.AccountInvalid, Assert.ThrowsExactly<ExecutionResourceException>(() =>
            zeroConcurrency.CreateAccount(new("root-zero"), null, new(MaxConcurrentAttempts: 0), Identity, "usd")).Code);
    }

    // ---- helpers ----

    private static InMemoryExecutionBudgetLedger NewLedger(long? maxOutput = 100, decimal? maxCost = null,
        int concurrency = 4, long? maxInputPerRequest = null)
    {
        var ledger = new InMemoryExecutionBudgetLedger();
        ledger.CreateAccount(Root, null, new(maxOutput, maxCost, null, concurrency, maxInputPerRequest), Identity, "usd");
        ledger.CreateAccount(Child, Root, new(), Identity, "usd");
        ledger.CreateAccount(new("child-2"), Root, new(), Identity, "usd");
        return ledger;
    }

    private static ReserveBudget Reserve(BudgetAccountId account, ImmutableArray<BudgetAccountId> path, string operationId,
        string attemptId, long maxOutput = 10, long maxInput = 100, decimal maxCost = .1m, string costUnit = "usd",
        DateTimeOffset? deadline = null) => new(operationId, attemptId, account, path, Identity,
        "provider/model", "model-v1", "price-v1", maxInput, maxOutput, maxCost, costUnit,
        deadline ?? DateTimeOffset.UtcNow.AddMinutes(2));

    private static SettleBudget Settle(BudgetReservation reservation, long ownerEpoch, ModelUsage usage, decimal cost,
        string receipt) => new(reservation.ReservationId, reservation.AttemptId, ownerEpoch, usage, cost, reservation.CostUnit,
        receipt, DateTimeOffset.UtcNow);

    private static async Task<ExecutionResourceException> FailsAsync<T>(Func<ValueTask<T>> action)
        => await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () => await action().ConfigureAwait(false));

    private static async Task<ExecutionResourceException> FailsAsync(Func<ValueTask> action)
        => await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () => await action().ConfigureAwait(false));
}
