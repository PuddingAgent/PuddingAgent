using System.Collections.Immutable;
using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResources;

/// <summary>
/// PA-01 attempt protocol over the durable budget ledger: reserve before the provider is called,
/// linearize dispatch against the cancellation revision, settle once, and keep unknown spend unknown.
/// One instance is bound to one host owner epoch; a restarted host uses a higher epoch and a fresh coordinator.
/// </summary>
public sealed class DurableBudgetCoordinator
{
    private readonly IExecutionBudgetLedger _ledger;

    public DurableBudgetCoordinator(IExecutionBudgetLedger ledger, long ownerEpoch)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _ownerEpoch = ownerEpoch >= 1 ? ownerEpoch : throw new ArgumentOutOfRangeException(nameof(ownerEpoch), "An owner epoch of at least 1 is required.");
    }

    private readonly long _ownerEpoch;
    public long OwnerEpoch => _ownerEpoch;

    public async ValueTask<DurableBudgetAttempt> BeginAsync(ReserveBudget command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reservation = await _ledger.ReserveAsync(command, ct).ConfigureAwait(false);
        return new DurableBudgetAttempt(_ledger, _ownerEpoch, reservation);
    }

    /// <summary>
    /// One provider attempt: reserve → dispatch → invoke → settle/unknown. A zero or exhausted budget
    /// fails at reservation time, so the provider spy is never called. Buffered and streaming channels share this seam.
    /// </summary>
    public async ValueTask<T> ExecuteAsync<T>(ReserveBudget command, Func<CancellationToken, Task<T>> provider,
        Func<T, ModelUsage?> usage, Func<T, decimal> cost, Func<T, string> receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await using var attempt = await BeginAsync(command, ct).ConfigureAwait(false);
        // The dispatch linearization point: a cancellation recorded before this line rejects the dispatch,
        // so the provider is never called after a cancelled root.
        await attempt.DispatchAsync(ct).ConfigureAwait(false);
        T outcome;
        try
        {
            outcome = await provider(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispatched without an observed response: the provider may have charged. Keep the ceiling unknown
            // and never publish a late success. The original exception wins over a bookkeeping failure.
            try { await attempt.MarkUnknownAsync("provider.failed", CancellationToken.None).ConfigureAwait(false); }
            catch (ExecutionResourceException) { }
            throw;
        }
        var actual = usage(outcome);
        if (actual is null)
        {
            await attempt.MarkUnknownAsync("provider.usage_missing", CancellationToken.None).ConfigureAwait(false);
            throw new ExecutionResourceException(BudgetErrorCodes.UsageUnknown,
                "Provider usage is unavailable; the reservation stays unknown and is not refunded.");
        }
        await attempt.SettleAsync(actual, cost(outcome), receipt(outcome), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Restart reconciliation. Undispatched reservations are refunded; dispatched or unknown attempts stay
    /// unknown (a TTL or a missing receipt is not proof the provider stopped or did not charge).
    /// </summary>
    public async ValueTask<ImmutableArray<BudgetRecoveryAction>> RecoverAsync(BudgetAccountId account, CancellationToken ct = default)
    {
        if (_ledger is not IExecutionBudgetRecoveryLedger recovery)
            throw new InvalidOperationException("The configured ledger has no recovery listing.");
        var candidates = await recovery.ListUnsettledAsync(account, ct).ConfigureAwait(false);
        var actions = BudgetRecoveryPlanner.Plan(candidates);
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case BudgetRecoveryActionKind.ReleaseUndispatched:
                    await _ledger.ReleaseAsync(new(action.ReservationId, action.AttemptId), ct).ConfigureAwait(false);
                    break;
                case BudgetRecoveryActionKind.KeepUnknown:
                    await _ledger.MarkUnknownAsync(
                        new(action.ReservationId, action.AttemptId, action.OwnerEpoch, BudgetErrorCodes.UsageUnknown), ct)
                        .ConfigureAwait(false);
                    break;
            }
        }
        return actions;
    }
}

/// <summary>Handle for one reserved attempt. Exactly one dispatch, then exactly one settle or unknown; an abandoned pending handle refunds on dispose.</summary>
public sealed class DurableBudgetAttempt : IAsyncDisposable
{
    private readonly IExecutionBudgetLedger _ledger;
    private readonly long _ownerEpoch;
    private bool _dispatched;
    private bool _finished;

    internal DurableBudgetAttempt(IExecutionBudgetLedger ledger, long ownerEpoch, BudgetReservation reservation)
    {
        _ledger = ledger;
        _ownerEpoch = ownerEpoch;
        Reservation = reservation;
    }

    public BudgetReservation Reservation { get; }

    public async ValueTask DispatchAsync(CancellationToken ct = default)
    {
        if (_dispatched || _finished)
            throw new ExecutionResourceException(BudgetErrorCodes.DispatchInvalid, "Each attempt permits exactly one dispatch.");
        await _ledger.MarkDispatchedAsync(new(Reservation.ReservationId, Reservation.AttemptId, _ownerEpoch,
            Reservation.CancellationRevision), ct).ConfigureAwait(false);
        _dispatched = true;
    }

    public async ValueTask<SettlementReceipt> SettleAsync(ModelUsage usage, decimal actualCost, string providerReceipt,
        DateTimeOffset occurredAt, CancellationToken ct = default)
    {
        if (!_dispatched || _finished)
            throw new ExecutionResourceException(BudgetErrorCodes.SettlementInvalid, "Only one dispatched attempt can be settled.");
        var receipt = await _ledger.SettleAsync(new(Reservation.ReservationId, Reservation.AttemptId, _ownerEpoch,
            usage, actualCost, Reservation.CostUnit, providerReceipt, occurredAt), ct).ConfigureAwait(false);
        _finished = true;
        return receipt;
    }

    public async ValueTask<BudgetSnapshot> MarkUnknownAsync(string reasonCode, CancellationToken ct = default)
    {
        if (!_dispatched || _finished)
            throw new ExecutionResourceException(BudgetErrorCodes.DispatchInvalid, "Only a live dispatched attempt can become unknown.");
        var snapshot = await _ledger.MarkUnknownAsync(new(Reservation.ReservationId, Reservation.AttemptId,
            _ownerEpoch, reasonCode), ct).ConfigureAwait(false);
        _finished = true;
        return snapshot;
    }

    public async ValueTask ReleaseAsync(CancellationToken ct = default)
    {
        if (_dispatched || _finished)
            throw new ExecutionResourceException(BudgetErrorCodes.DispatchInvalid, "Only an undispatched attempt can be released.");
        await _ledger.ReleaseAsync(new(Reservation.ReservationId, Reservation.AttemptId), ct).ConfigureAwait(false);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_finished) return;
        try
        {
            // An abandoned pending attempt refunds; an abandoned dispatched attempt keeps its ceiling unknown.
            if (_dispatched) await MarkUnknownAsync("attempt.abandoned").ConfigureAwait(false);
            else await ReleaseAsync().ConfigureAwait(false);
        }
        catch (ExecutionResourceException) { }
    }
}

public enum BudgetRecoveryActionKind { ReleaseUndispatched, KeepUnknown }

public sealed record BudgetRecoveryAction(BudgetRecoveryActionKind Kind, string ReservationId, string AttemptId, long OwnerEpoch);

/// <summary>Pure planning step, separated from storage so the restart reconciler can be tested without a database.</summary>
public static class BudgetRecoveryPlanner
{
    public static ImmutableArray<BudgetRecoveryAction> Plan(IEnumerable<BudgetRecoveryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var releases = new List<BudgetRecoveryAction>();
        var unknowns = new List<BudgetRecoveryAction>();
        foreach (var candidate in candidates.OrderBy(c => c.ReservationId, StringComparer.Ordinal))
        {
            if (!seen.Add(candidate.ReservationId + "|" + candidate.AttemptId)) continue;
            var action = candidate.Phase == BudgetAttemptPhase.Pending
                ? new BudgetRecoveryAction(BudgetRecoveryActionKind.ReleaseUndispatched, candidate.ReservationId, candidate.AttemptId, candidate.OwnerEpoch)
                : new BudgetRecoveryAction(BudgetRecoveryActionKind.KeepUnknown, candidate.ReservationId, candidate.AttemptId, candidate.OwnerEpoch);
            (candidate.Phase == BudgetAttemptPhase.Pending ? releases : unknowns).Add(action);
        }
        return [.. releases, .. unknowns];
    }
}
