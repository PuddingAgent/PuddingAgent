using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 / 07A boundary: the product sink receives one committed review-completed fact (the host maps it to
/// canonical conversation/Goal events). Delivery is at least once; the dispatcher only marks published after the
/// sink accepted the event.
/// </summary>
public interface IReviewCompletionSink
{
    ValueTask PublishAsync(ReviewCompletedEvent completed, CancellationToken ct = default);
}

public sealed record ReviewDispatchResult(int Attempted, int Published, int Failed);

/// <summary>
/// PA-03 S5 outbox dispatcher: reads unpublished completed-review facts in occurred order, hands each to the sink
/// at most once per batch, and marks published only on success. A failed sink call leaves the row unpublished for
/// the next pass (at least once); a crash between publish and mark may repeat the fact, which consumers must key by
/// EventId. No event is dropped or silently acknowledged.
/// </summary>
public sealed class ReviewEventDispatcher(IDbContextFactory<PlatformDbContext> dbFactory,
    IReviewCompletionSink sink, ILogger? logger = null)
{
    public async Task<ReviewDispatchResult> DispatchBatchAsync(int maxCount, CancellationToken ct = default)
    {
        if (maxCount is < 1 or > 100)
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "The dispatch batch size is out of bounds.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ReviewEvents.AsNoTracking()
            .Where(e => !e.Published).OrderBy(e => e.OccurredAtUnixMs).ThenBy(e => e.EventId).Take(maxCount)
            .ToListAsync(ct).ConfigureAwait(false);
        var published = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            var completed = new ReviewCompletedEvent(row.EventId, row.ReviewId, row.JobId,
                new ReviewLogicalKey(row.WorkspaceId, row.ParentWorkId, row.GoalRunId, row.ActivationEpoch,
                    row.IterationNo, row.ObjectiveVersion, row.AcceptanceContractVersion, row.ArtifactSetHash,
                    row.ReviewPolicyVersion),
                (ReviewFindingValue)row.Finding, (ReviewEvidenceLevel)row.Level, row.Revision, row.OccurredAt);
            try
            {
                await sink.PublishAsync(completed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                logger?.LogWarning(ex, "PA-03 review event {EventId} could not be delivered.", row.EventId);
                continue;
            }
            await using var write = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var tracked = await write.ReviewEvents.SingleOrDefaultAsync(e => e.EventId == row.EventId, ct)
                .ConfigureAwait(false);
            if (tracked is null || tracked.Published) continue;
            tracked.Published = true;
            tracked.PublishedAt = DateTimeOffset.UtcNow;
            await write.SaveChangesAsync(ct).ConfigureAwait(false);
            published++;
        }
        return new(rows.Count, published, failed);
    }
}
