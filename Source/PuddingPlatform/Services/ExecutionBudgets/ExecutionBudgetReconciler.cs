using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 S5 restart reconciliation: scans every host-owned budget account and only touches unsettled attempts.
/// A pending attempt (never dispatched) is released (safe refund); a dispatched attempt is recorded as unknown
/// because the provider outcome is unknown — its budget stays unavailable and is never refunded; an already
/// unknown attempt is retained. The pass is idempotent and a concurrent settlement/conflict never fabricates
/// success or loses the reservation.
/// </summary>
public sealed class ExecutionBudgetReconciler(IDbContextFactory<PlatformDbContext> dbFactory)
{
    public async Task<BudgetReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        var store = new ExecutionBudgetStore(dbFactory);
        var accountIds = await store.ListAccountIdsAsync(ct).ConfigureAwait(false);
        var released = 0;
        var markedUnknown = 0;
        var retained = 0;
        foreach (var accountId in accountIds)
        {
            ImmutableArray<BudgetRecoveryCandidate> candidates;
            try
            {
                candidates = await store.ListUnsettledAsync(accountId, ct).ConfigureAwait(false);
            }
            catch (ExecutionResourceException)
            {
                continue; // an account removed concurrently has nothing to reconcile
            }
            foreach (var candidate in candidates)
            {
                switch (candidate.Phase)
                {
                    case BudgetAttemptPhase.Pending:
                        try
                        {
                            await store.ReleaseAsync(new(candidate.ReservationId, candidate.AttemptId), ct)
                                .ConfigureAwait(false);
                            released++;
                        }
                        catch (ExecutionResourceException)
                        {
                            retained++; // settled/dispatched concurrently; never force a refund
                        }
                        break;
                    case BudgetAttemptPhase.Dispatched:
                        try
                        {
                            await store.MarkUnknownAsync(new(candidate.ReservationId, candidate.AttemptId,
                                candidate.OwnerEpoch, "reconcile.dispatch_unknown"), ct).ConfigureAwait(false);
                            markedUnknown++;
                        }
                        catch (ExecutionResourceException)
                        {
                            retained++; // another owner settled or fenced first
                        }
                        break;
                    default:
                        retained++;
                        break;
                }
            }
        }
        return new(accountIds.Length, released, markedUnknown, retained);
    }
}

public sealed record BudgetReconcileReport(int Accounts, int ReleasedPending, int MarkedUnknown, int RetainedUnknown);
