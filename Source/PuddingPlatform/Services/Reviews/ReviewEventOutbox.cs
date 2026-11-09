using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 completed-review outbox: writes the completed fact in the same store transaction seam as the job
/// completion (the caller publishes after CompleteAsync); re-publishing the same event id is idempotent.
/// A later dispatcher (07A S5) marks rows published — this adapter never drops an unpublished fact.
/// </summary>
public sealed class ReviewEventOutbox(IDbContextFactory<PlatformDbContext> dbFactory) : IReviewEventOutbox
{
    public async ValueTask PublishCompletedAsync(ReviewCompletedEvent completed, CancellationToken ct = default)
    {
        if (completed is null || string.IsNullOrWhiteSpace(completed.EventId) || completed.EventId.Length > 128
            || string.IsNullOrWhiteSpace(completed.JobId) || string.IsNullOrWhiteSpace(completed.ReviewId))
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A valid completed review event is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.ReviewEvents.AnyAsync(e => e.EventId == completed.EventId, ct).ConfigureAwait(false))
            return;
        db.ReviewEvents.Add(new ReviewEventEntity
        {
            EventId = completed.EventId,
            ReviewId = completed.ReviewId,
            JobId = completed.JobId,
            WorkspaceId = completed.Key.WorkspaceId,
            ParentWorkId = completed.Key.ParentWorkId,
            GoalRunId = completed.Key.GoalRunId,
            ActivationEpoch = completed.Key.ActivationEpoch,
            IterationNo = completed.Key.IterationNo,
            ObjectiveVersion = completed.Key.ObjectiveVersion,
            AcceptanceContractVersion = completed.Key.AcceptanceContractVersion,
            ArtifactSetHash = completed.Key.ArtifactSetHash,
            ReviewPolicyVersion = completed.Key.ReviewPolicyVersion,
            Finding = (int)completed.Finding,
            Level = (int)completed.Level,
            Revision = completed.Revision,
            OccurredAt = completed.OccurredAt,
            OccurredAtUnixMs = completed.OccurredAt.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
