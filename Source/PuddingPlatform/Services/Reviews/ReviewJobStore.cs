using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 durable review job store on SQLite: idempotent submit by logical key AND operation id (a reused
/// operation with different content conflicts), owner-epoch lease claims with expected revision (an old worker's
/// claim is rejected), once-only completion/failure from Running under the same owner, and cancellation only for
/// non-terminal jobs. Findings stay review-job results; this store never writes a Goal terminal state.
/// </summary>
public sealed class ReviewJobStore(IDbContextFactory<PlatformDbContext> dbFactory)
    : IReviewJobStore, IReviewJobByGoalReader, IReviewJobByGoalObjectiveReader
{
    private const int Pending = 0;
    private const int Running = 1;
    private const int Waiting = 2;
    private const int Completed = 3;
    private const int Failed = 4;
    private const int Unknown = 5;
    private const int Cancelled = 6;

    public async ValueTask<ReviewCreateResult> CreateOrGetAsync(ReviewJobRecord record, string operationId,
        CancellationToken ct = default)
    {
        if (record is null || !record.IsValid || string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw Error(ReviewErrorCodes.RequestInvalid, "A valid review job record and operation id are required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var operation = await db.ReviewJobOperations.SingleOrDefaultAsync(o => o.OperationId == operationId, ct)
            .ConfigureAwait(false);
        if (operation is not null)
        {
            var existing = await db.ReviewJobs.SingleAsync(j => j.JobId == operation.JobId, ct).ConfigureAwait(false);
            if (!SameKey(existing, record.Key) || !SameCreation(existing, record))
                throw Error(ReviewErrorCodes.Conflict, "The operation id was already used for a different review job.");
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(ToView(existing), Created: false);
        }
        var found = await FindByKeyAsync(db, record.Key, ct).ConfigureAwait(false);
        if (found is not null)
        {
            db.ReviewJobOperations.Add(new() { OperationId = operationId, JobId = found.JobId, ReviewId = found.ReviewId });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(ToView(found), Created: false);
        }
        var now = DateTimeOffset.UtcNow;
        var acceptedSequence = record.AcceptedSequence > 0
            ? record.AcceptedSequence
            : (await db.ReviewJobs.Select(j => (long?)j.AcceptedSequence).MaxAsync(ct).ConfigureAwait(false) ?? 0) + 1;
        var entity = new ReviewJobEntity
        {
            JobId = record.JobId,
            ReviewId = record.ReviewId,
            WorkspaceId = record.Key.WorkspaceId,
            ParentWorkId = record.Key.ParentWorkId,
            GoalRunId = record.Key.GoalRunId,
            ActivationEpoch = record.Key.ActivationEpoch,
            IterationNo = record.Key.IterationNo,
            ObjectiveVersion = record.Key.ObjectiveVersion,
            AcceptanceContractVersion = record.Key.AcceptanceContractVersion,
            ArtifactSetHash = record.Key.ArtifactSetHash,
            ReviewPolicyVersion = record.Key.ReviewPolicyVersion,
            Required = record.Required,
            BudgetAccountId = record.BudgetAccountId,
            ActorId = record.ActorId,
            Deadline = record.Deadline,
            AcceptedAt = record.AcceptedAt,
            AcceptedSequence = acceptedSequence,
            State = Pending,
            Revision = 0,
            UpdatedAt = now,
        };
        db.ReviewJobs.Add(entity);
        db.ReviewJobOperations.Add(new() { OperationId = operationId, JobId = record.JobId, ReviewId = record.ReviewId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(ToView(entity), Created: true);
    }

    public async ValueTask<ReviewJobView?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ReviewJobs.AsNoTracking().SingleOrDefaultAsync(j => j.JobId == jobId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToView(entity);
    }

    /// <summary>Latest review job for a Goal run (by accepted sequence); null when none exists.</summary>
    /// <summary>
    /// I5 completion gate lookup: the newest accepted job for the exact Goal objective version. A newer job for another
    /// version must not shadow the review that actually belongs to this candidate.
    /// </summary>
    public async Task<ReviewJobView?> GetLatestByGoalObjectiveAsync(string goalRunId, string objectiveVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goalRunId) || goalRunId.Length > 128
            || string.IsNullOrWhiteSpace(objectiveVersion) || objectiveVersion.Length > 32)
            throw Error(ReviewErrorCodes.RequestInvalid, "A bounded Goal review lookup is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ReviewJobs.AsNoTracking()
            .Where(j => j.GoalRunId == goalRunId && j.ObjectiveVersion == objectiveVersion)
            .OrderByDescending(j => j.AcceptedSequence).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return entity is null ? null : ToView(entity);
    }
    public async Task<ReviewJobView?> GetLatestByGoalAsync(string goalRunId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goalRunId) || goalRunId.Length > 128) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ReviewJobs.AsNoTracking()
            .Where(j => j.GoalRunId == goalRunId)
            .OrderByDescending(j => j.AcceptedSequence).ThenByDescending(j => j.JobId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return entity is null ? null : ToView(entity);
    }
    public async Task<ImmutableArray<string>> ListClaimableAsync(int maxCount, CancellationToken ct = default)
    {
        if (maxCount is < 1 or > 100) throw Error(ReviewErrorCodes.RequestInvalid, "The claim batch size is out of bounds.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var ids = await db.ReviewJobs.AsNoTracking()
            .Where(j => j.State == Pending || j.State == Waiting
                        || (j.State == Running && j.LeaseUntilUnixMs != null && j.LeaseUntilUnixMs <= nowMs))
            .OrderBy(j => j.AcceptedSequence).Take(maxCount).Select(j => j.JobId).ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. ids];
    }
    public async ValueTask<ReviewJobView?> GetByReviewAsync(string reviewId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ReviewJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ReviewId == reviewId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToView(entity);
    }

    public async ValueTask<ReviewJobView> ClaimAsync(string jobId, long ownerEpoch, DateTimeOffset leaseUntil,
        long expectedRevision, CancellationToken ct = default)
    {
        if (ownerEpoch < 1 || leaseUntil <= DateTimeOffset.UtcNow)
            throw Error(ReviewErrorCodes.RequestInvalid, "A positive owner epoch and future lease are required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        if (entity.Revision != expectedRevision)
            throw Error(ReviewErrorCodes.RevisionConflict, "The review job revision changed.");
        var claimable = entity.State is Pending or Waiting
            || (entity.State == Running && entity.LeaseUntilUnixMs is { } lease && lease <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!claimable) throw Error(ReviewErrorCodes.StateInvalid, "The review job is not claimable.");
        if (ownerEpoch <= entity.OwnerEpoch && entity.State == Running)
            throw Error(ReviewErrorCodes.OwnerStale, "A reclaimed review job requires a strictly newer owner epoch.");
        entity.State = Running;
        entity.OwnerEpoch = ownerEpoch;
        entity.LeaseUntilUnixMs = leaseUntil.ToUnixTimeMilliseconds();
        entity.Revision++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToView(entity);
    }

    /// <summary>
    /// Atomic completion: the terminal job row and its immutable review.completed outbox fact are written in one
    /// transaction. The same completion replay returns the stored fact; a different content under the same event id
    /// is rejected. There is no completion path that writes the terminal state without the event.
    /// </summary>
    public async ValueTask<ReviewJobView> CompleteAsync(string jobId, long ownerEpoch, long expectedRevision,
        ReviewOutcome outcome, ReviewCompletedEvent completed, CancellationToken ct = default)
    {
        if (outcome is null || !outcome.Criteria.IsDefault
            && outcome.Criteria.Any(c => c is null || string.IsNullOrWhiteSpace(c.CriterionId)))
            throw Error(ReviewErrorCodes.RequestInvalid, "A valid review outcome is required.");
        if (completed is null || string.IsNullOrWhiteSpace(completed.EventId) || completed.EventId.Length > 128
            || !string.Equals(completed.JobId, jobId, StringComparison.Ordinal)
            || completed.Revision != expectedRevision + 1)
            throw Error(ReviewErrorCodes.RequestInvalid, "A stable completion event bound to the job revision is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        var sameOutcome = entity.OutcomeFinding == (int)outcome!.Finding
            && entity.OutcomeLevel == (int)outcome.Level
            && string.Equals(entity.OutcomeCriteriaJson, JsonSerializer.Serialize(outcome.Criteria), StringComparison.Ordinal)
            && string.Equals(entity.ReasonCode, outcome.ReasonCode, StringComparison.Ordinal);
        if (entity.State == Completed)
        {
            // Idempotent replay of the exact same completion: no second event, no state change.
            var stored = await db.ReviewEvents.AsNoTracking()
                .SingleOrDefaultAsync(e => e.EventId == completed.EventId, ct).ConfigureAwait(false);
            if (sameOutcome && entity.Revision == expectedRevision + 1 && entity.OwnerEpoch == ownerEpoch
                && stored is not null && string.Equals(stored.JobId, jobId, StringComparison.Ordinal))
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return ToView(entity);
            }
            throw Error(ReviewErrorCodes.StateInvalid, "The review job already completed with different content.");
        }
        EnsureRunningUnderOwner(entity, ownerEpoch, expectedRevision);
        entity.State = Completed;
        entity.OutcomeFinding = (int)outcome!.Finding;
        entity.OutcomeLevel = (int)outcome.Level;
        entity.OutcomeCriteriaJson = JsonSerializer.Serialize(outcome.Criteria);
        entity.ReasonCode = outcome.ReasonCode;
        entity.Revision++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        var existingEvent = await db.ReviewEvents.SingleOrDefaultAsync(e => e.EventId == completed.EventId, ct)
            .ConfigureAwait(false);
        if (existingEvent is null)
        {
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
        }
        else if (!string.Equals(existingEvent.JobId, jobId, StringComparison.Ordinal))
        {
            throw Error(ReviewErrorCodes.Conflict, "The completion event id belongs to another review job.");
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ToView(entity);
    }

    public async ValueTask<ReviewJobView> FailAsync(string jobId, long ownerEpoch, long expectedRevision,
        ReviewJobState state, string reasonCode, CancellationToken ct = default)
    {
        if (state is not (ReviewJobState.Failed or ReviewJobState.Unknown or ReviewJobState.Waiting))
            throw Error(ReviewErrorCodes.RequestInvalid, "A failure state must be Failed, Unknown or Waiting.");
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128)
            throw Error(ReviewErrorCodes.RequestInvalid, "A bounded failure reason is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        EnsureRunningUnderOwner(entity, ownerEpoch, expectedRevision);
        entity.State = state switch
        {
            ReviewJobState.Failed => Failed,
            ReviewJobState.Waiting => Waiting,
            _ => Unknown,
        };
        entity.ReasonCode = reasonCode;
        entity.Revision++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToView(entity);
    }

    public async ValueTask<ReviewJobView> CancelAsync(string jobId, long expectedRevision, string reasonCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128)
            throw Error(ReviewErrorCodes.RequestInvalid, "A bounded cancellation reason is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        if (entity.Revision != expectedRevision)
            throw Error(ReviewErrorCodes.RevisionConflict, "The review job revision changed.");
        if (entity.State is Completed or Failed or Cancelled)
            throw Error(ReviewErrorCodes.StateInvalid, "A terminal review job cannot be cancelled.");
        entity.State = Cancelled;
        entity.ReasonCode = reasonCode;
        entity.Revision++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToView(entity);
    }

    private static void EnsureRunningUnderOwner(ReviewJobEntity entity, long ownerEpoch, long expectedRevision)
    {
        if (entity.State != Running) throw Error(ReviewErrorCodes.StateInvalid, "The review job is not running.");
        if (entity.OwnerEpoch != ownerEpoch)
            throw Error(ReviewErrorCodes.OwnerStale, "Only the current owner can finish this review job.");
        if (entity.Revision != expectedRevision)
            throw Error(ReviewErrorCodes.RevisionConflict, "The review job revision changed.");
    }

    private static async Task<ReviewJobEntity> FindAsync(PlatformDbContext db, string jobId, CancellationToken ct)
        => await db.ReviewJobs.SingleOrDefaultAsync(j => j.JobId == jobId, ct).ConfigureAwait(false)
            ?? throw Error(ReviewErrorCodes.NotFound, "The review job is missing.");

    private static async Task<ReviewJobEntity?> FindByKeyAsync(PlatformDbContext db, ReviewLogicalKey key,
        CancellationToken ct)
        => await db.ReviewJobs.AsNoTracking().SingleOrDefaultAsync(j => j.WorkspaceId == key.WorkspaceId
            && j.ParentWorkId == key.ParentWorkId && j.GoalRunId == key.GoalRunId
            && j.ActivationEpoch == key.ActivationEpoch && j.IterationNo == key.IterationNo
            && j.ObjectiveVersion == key.ObjectiveVersion
            && j.AcceptanceContractVersion == key.AcceptanceContractVersion
            && j.ArtifactSetHash == key.ArtifactSetHash && j.ReviewPolicyVersion == key.ReviewPolicyVersion, ct)
            .ConfigureAwait(false);

    private static bool SameCreation(ReviewJobEntity entity, ReviewJobRecord record) =>
        entity.Required == record.Required
        && string.Equals(entity.BudgetAccountId, record.BudgetAccountId, StringComparison.Ordinal)
        && string.Equals(entity.ActorId, record.ActorId, StringComparison.Ordinal);

    private static bool SameKey(ReviewJobEntity entity, ReviewLogicalKey key)
        => entity.WorkspaceId == key.WorkspaceId && entity.ParentWorkId == key.ParentWorkId
           && entity.GoalRunId == key.GoalRunId && entity.ActivationEpoch == key.ActivationEpoch
           && entity.IterationNo == key.IterationNo && entity.ObjectiveVersion == key.ObjectiveVersion
           && entity.AcceptanceContractVersion == key.AcceptanceContractVersion
           && entity.ArtifactSetHash == key.ArtifactSetHash && entity.ReviewPolicyVersion == key.ReviewPolicyVersion;

    private static ReviewJobView ToView(ReviewJobEntity e) => new(e.JobId, e.ReviewId, Key(e), e.Required,
        e.BudgetAccountId, e.Deadline, e.State switch
        {
            Pending => ReviewJobState.Pending,
            Running => ReviewJobState.Running,
            Waiting => ReviewJobState.Waiting,
            Completed => ReviewJobState.Completed,
            Failed => ReviewJobState.Failed,
            Unknown => ReviewJobState.Unknown,
            _ => ReviewJobState.Cancelled,
        }, e.Revision, e.OwnerEpoch, e.AcceptedSequence, Outcome(e), e.ReasonCode, e.UpdatedAt, e.ActorId);

    private static ReviewLogicalKey Key(ReviewJobEntity e) => new(e.WorkspaceId, e.ParentWorkId, e.GoalRunId,
        e.ActivationEpoch, e.IterationNo, e.ObjectiveVersion, e.AcceptanceContractVersion, e.ArtifactSetHash,
        e.ReviewPolicyVersion);

    private static ReviewOutcome? Outcome(ReviewJobEntity e) => e.OutcomeFinding is not { } finding
        ? null
        : new((ReviewFindingValue)finding, (ReviewEvidenceLevel)(e.OutcomeLevel ?? 0),
            e.OutcomeCriteriaJson is null
                ? ImmutableArray<ReviewCriterionOutcome>.Empty
                : JsonSerializer.Deserialize<ImmutableArray<ReviewCriterionOutcome>>(e.OutcomeCriteriaJson),
            e.ReasonCode);

    private static ReviewException Error(string code, string message) => new(code, message);
}
