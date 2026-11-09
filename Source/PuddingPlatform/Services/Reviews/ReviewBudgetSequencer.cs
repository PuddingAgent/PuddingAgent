using PuddingExecutionResources.Contracts;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>An in-flight review attempt budget: reserved, dispatched, and awaiting settlement/unknown.</summary>
public sealed record ReviewAttemptBudget(BudgetReservation Reservation, string AttemptId, DispatchPermit Permit)
{
    public long OwnerEpoch => Permit.OwnerEpoch;
}

/// <summary>
/// PA-03/PA-01 glue: the review worker's attempt budget must be reserved, dispatched and then either settled with
/// real usage or marked unknown. This sequencer never refunds a dispatched attempt (unknown is not a refund), never
/// settles with a stale owner, and releases a reservation only when dispatch itself fails before the linearization
/// point.
/// </summary>
public sealed class ReviewBudgetSequencer(IExecutionBudgetLedger ledger)
{
    public async ValueTask<ReviewAttemptBudget> BeginAsync(ReserveBudget command, long ownerEpoch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (ownerEpoch < 1) throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A positive owner epoch is required.");
        var reservation = await ledger.ReserveAsync(command, ct).ConfigureAwait(false);
        DispatchPermit permit;
        try
        {
            permit = await ledger.MarkDispatchedAsync(new DispatchBudget(reservation.ReservationId, command.AttemptId,
                ownerEpoch, reservation.CancellationRevision), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispatch did not cross the linearization point: refund the pending reservation and surface the failure.
            await ledger.ReleaseAsync(new(reservation.ReservationId, command.AttemptId), ct).ConfigureAwait(false);
            throw;
        }
        return new(reservation, command.AttemptId, permit);
    }

    public async ValueTask<SettlementReceipt> SettleAsync(ReviewAttemptBudget budget, ModelUsage usage,
        decimal actualCost, string costUnit, string providerReceipt, DateTimeOffset occurredAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return await ledger.SettleAsync(new(budget.Reservation.ReservationId, budget.AttemptId, budget.OwnerEpoch,
            usage, actualCost, costUnit, providerReceipt, occurredAt), ct).ConfigureAwait(false);
    }

    /// <summary>The provider outcome is unknown: amounts stay unavailable and are never silently refunded.</summary>
    public async ValueTask<BudgetSnapshot> AbandonAsync(ReviewAttemptBudget budget, string reasonCode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128)
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A bounded abandon reason is required.");
        return await ledger.MarkUnknownAsync(new(budget.Reservation.ReservationId, budget.AttemptId,
            budget.OwnerEpoch, reasonCode), ct).ConfigureAwait(false);
    }
}
