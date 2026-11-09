using PuddingExecutionResources.Contracts;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

public sealed record ReviewAttemptReconciliation(string Status, int UnsettledAttempts, string? ReasonCode)
{
    public bool Blocked => Status == "Blocked";
}

/// <summary>
/// PA-03/PA-01 seam: an expired Running review may already have a dispatched model attempt. Retrying before that
/// attempt is settled or marked unknown would pay for the same review twice, so the worker defers until the durable
/// ledger has no unsettled attempt for this job (attempt ids are <c>{jobId}:{ownerEpoch}</c>).
/// </summary>
public sealed class ReviewAttemptReconciler(IExecutionBudgetRecoveryLedger ledger)
{
    public async Task<ReviewAttemptReconciliation> InspectAsync(ReviewJobView job, CancellationToken ct = default)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.BudgetAccountId))
            return new("Clear", 0, null);
        var unsettled = await ledger.ListUnsettledAsync(new(job.BudgetAccountId), ct).ConfigureAwait(false);
        var mine = unsettled
            .Where(a => a.AttemptId.StartsWith(job.JobId + ":", StringComparison.Ordinal)
                        && a.Phase is BudgetAttemptPhase.Pending or BudgetAttemptPhase.Dispatched)
            .ToArray();
        return mine.Length == 0
            ? new("Clear", 0, null)
            : new("Blocked", mine.Length, "review.attempt_unsettled");
    }
}
