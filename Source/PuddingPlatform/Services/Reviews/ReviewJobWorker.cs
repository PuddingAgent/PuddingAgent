using Microsoft.Extensions.Logging;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

public sealed record ReviewWorkerBatchResult(int Claimed, int Executed, int Skipped, int Failed);

/// <summary>
/// PA-03 S5 review worker: one bounded batch —— list claimable jobs (Pending or expired Running lease), then run
/// each through the independent coordinator (claim → evidence → executor → validate → complete/fail → outbox).
/// Terminal jobs are never claimed; a per-attempt monotonically increasing owner epoch fences a stale worker.
/// </summary>
public sealed class ReviewJobWorker(ReviewJobStore store, ReviewJobCoordinator coordinator,
    TimeProvider? clock = null, ILogger? logger = null, ReviewAttemptReconciler? reconciler = null)
{
    private long _epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task<ReviewWorkerBatchResult> RunBatchAsync(int maxCount, TimeSpan lease,
        CancellationToken ct = default)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(1))
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "The lease is out of bounds.");
        var ids = await store.ListClaimableAsync(maxCount, ct).ConfigureAwait(false);
        var executed = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var jobId in ids)
        {
            if (ct.IsCancellationRequested) break;
            var ownerEpoch = Interlocked.Increment(ref _epoch);
            try
            {
                if (reconciler is not null)
                {
                    var current = await store.GetAsync(jobId, ct).ConfigureAwait(false);
                    if (current is { State: ReviewJobState.Running })
                    {
                        var reconciliation = await reconciler.InspectAsync(current, ct).ConfigureAwait(false);
                        if (reconciliation.Blocked)
                        {
                            skipped++;
                            logger?.LogWarning(
                                "PA-03 review job {JobId} has {Count} unsettled dispatched attempt(s); retry deferred until reconciliation.",
                                jobId, reconciliation.UnsettledAttempts);
                            continue;
                        }
                    }
                }
                var result = await coordinator.RunOnceAsync(jobId, ownerEpoch, lease, ct).ConfigureAwait(false);
                if (result.Executed) executed++; else skipped++;
            }
            catch (ReviewException ex)
            {
                failed++;
                logger?.LogWarning(ex, "PA-03 review worker could not run job {JobId}: {Code}", jobId, ex.Code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger?.LogWarning(ex, "PA-03 review worker failed job {JobId}.", jobId);
            }
        }
        return new(ids.Length, executed, skipped, failed);
    }
}
