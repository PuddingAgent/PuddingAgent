using System.Collections.Immutable;
using PuddingReviews.Contracts;

namespace PuddingReviewsTests;

/// <summary>Independent in-memory adapter for the PA-03 ports: idempotent submit, revision-checked claim/complete,
/// owner-epoch fencing, cancel and a completion outbox. Not a production store.</summary>
public sealed class ReviewTestHost : IReviewJobStore, IReviewEvidenceReader, IReviewExecutor, IReviewEventOutbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ReviewJobView> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _jobByReview = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _jobByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, string JobId)> _operations = new(StringComparer.Ordinal);
    private readonly List<ReviewCompletedEvent> _events = [];
    private long _sequence;

    public ReviewEvidenceReadResult? Evidence { get; set; }
    public Func<ReviewJobView, ReviewEvidenceReadResult>? EvidenceFactory { get; set; }
    public Exception? EvidenceFailure { get; set; }
    public Func<ReviewInputSnapshot, ReviewModelResult>? Model { get; set; }
    public Exception? ExecutionFailure { get; set; }
    public Action? DuringExecution { get; set; }
    public int Executions { get; private set; }
    public int EvidenceReads { get; private set; }
    public IReadOnlyList<ReviewCompletedEvent> Events { get { lock (_gate) return _events.ToArray(); } }

    public ValueTask<ReviewCreateResult> CreateOrGetAsync(ReviewJobRecord record, string operationId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var fingerprint = Fingerprint(record.Key);
            if (_operations.TryGetValue(operationId, out var prior))
            {
                if (prior.Fingerprint != fingerprint)
                    throw new ReviewException(ReviewErrorCodes.Conflict,
                        "The operation id was already used with a different review key.");
                return ValueTask.FromResult(new ReviewCreateResult(_jobs[prior.JobId], false));
            }
            if (_jobByKey.TryGetValue(fingerprint, out var existingId))
            {
                _operations[operationId] = (fingerprint, existingId);
                return ValueTask.FromResult(new ReviewCreateResult(_jobs[existingId], false));
            }
            _sequence++;
            var view = new ReviewJobView(record.JobId, record.ReviewId, record.Key, record.Required,
                record.BudgetAccountId, record.Deadline, ReviewJobState.Pending, Revision: 0, OwnerEpoch: 0,
                record.AcceptedSequence == 0 ? _sequence : record.AcceptedSequence, Outcome: null, ReasonCode: null,
                DateTimeOffset.UtcNow);
            _jobs[record.JobId] = view;
            _jobByReview[record.ReviewId] = record.JobId;
            _jobByKey[fingerprint] = record.JobId;
            _operations[operationId] = (fingerprint, record.JobId);
            return ValueTask.FromResult(new ReviewCreateResult(view, true));
        }
    }

    public ValueTask<ReviewJobView?> GetAsync(string jobId, CancellationToken ct = default)
    {
        lock (_gate) return ValueTask.FromResult(_jobs.TryGetValue(jobId, out var view) ? view : null);
    }

    public ValueTask<ReviewJobView?> GetByReviewAsync(string reviewId, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_jobByReview.TryGetValue(reviewId, out var jobId) ? _jobs[jobId] : null);
    }

    public ValueTask<ReviewJobView> ClaimAsync(string jobId, long ownerEpoch, DateTimeOffset leaseUntil,
        long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var view = Require(jobId);
            if (RevisionMismatch(view, expectedRevision)) throw new ReviewException(ReviewErrorCodes.RevisionConflict,
                "The review revision changed.");
            if (view.State is ReviewJobState.Completed or ReviewJobState.Failed or ReviewJobState.Cancelled)
                throw new ReviewException(ReviewErrorCodes.StateInvalid, "A terminal review cannot be claimed.");
            if (view.State == ReviewJobState.Running)
                throw new ReviewException(ReviewErrorCodes.StateInvalid, "The review is already leased to a running owner.");
            if (ownerEpoch < view.OwnerEpoch)
                throw new ReviewException(ReviewErrorCodes.OwnerStale, "A stale owner cannot claim this review.");
            var claimed = view with { State = ReviewJobState.Running, OwnerEpoch = ownerEpoch, Revision = view.Revision + 1 };
            _jobs[jobId] = claimed;
            return ValueTask.FromResult(claimed);
        }
    }

    public ValueTask<ReviewJobView> CompleteAsync(string jobId, long ownerEpoch, long expectedRevision,
        ReviewOutcome outcome, ReviewCompletedEvent completed, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var view = Require(jobId);
            if (completed is null || completed.JobId != jobId || completed.Revision != expectedRevision + 1)
                throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A bound completion event is required.");
            if (view.State == ReviewJobState.Completed)
            {
                if (view.Revision == expectedRevision + 1 && view.OwnerEpoch == ownerEpoch
                    && _events.Any(e => e.EventId == completed.EventId && e.JobId == jobId))
                    return ValueTask.FromResult(view);
                throw new ReviewException(ReviewErrorCodes.StateInvalid, "The review already completed with different content.");
            }
            if (RevisionMismatch(view, expectedRevision)) throw new ReviewException(ReviewErrorCodes.RevisionConflict,
                "The review was cancelled or changed before completion.");
            if (view.State != ReviewJobState.Running)
                throw new ReviewException(ReviewErrorCodes.StateInvalid, "Only a running review can complete.");
            if (ownerEpoch != view.OwnerEpoch)
                throw new ReviewException(ReviewErrorCodes.OwnerStale, "A stale owner cannot complete this review.");
            var done = view with
            {
                State = ReviewJobState.Completed,
                Outcome = outcome,
                Revision = view.Revision + 1,
            };
            _jobs[jobId] = done;
            _events.Add(completed);
            return ValueTask.FromResult(done);
        }
    }

    public ValueTask<ReviewJobView> FailAsync(string jobId, long ownerEpoch, long expectedRevision,
        ReviewJobState state, string reasonCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var view = Require(jobId);
            if (RevisionMismatch(view, expectedRevision)) throw new ReviewException(ReviewErrorCodes.RevisionConflict,
                "The review was cancelled or changed before failure recording.");
            if (ownerEpoch != view.OwnerEpoch)
                throw new ReviewException(ReviewErrorCodes.OwnerStale, "A stale owner cannot record this failure.");
            var failed = view with { State = state, ReasonCode = reasonCode, Revision = view.Revision + 1 };
            _jobs[jobId] = failed;
            return ValueTask.FromResult(failed);
        }
    }

    public ValueTask<ReviewJobView> CancelAsync(string jobId, long expectedRevision, string reasonCode,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var view = Require(jobId);
            if (view.State is ReviewJobState.Completed or ReviewJobState.Failed or ReviewJobState.Cancelled)
                return ValueTask.FromResult(view);
            if (RevisionMismatch(view, expectedRevision)) throw new ReviewException(ReviewErrorCodes.RevisionConflict,
                "The review revision changed before cancellation.");
            var cancelled = view with { State = ReviewJobState.Cancelled, ReasonCode = reasonCode, Revision = view.Revision + 1 };
            _jobs[jobId] = cancelled;
            return ValueTask.FromResult(cancelled);
        }
    }

    public ValueTask<ReviewEvidenceReadResult> ReadAsync(ReviewJobView job, CancellationToken ct = default)
    {
        lock (_gate) EvidenceReads++;
        if (EvidenceFailure is not null) throw EvidenceFailure;
        if (EvidenceFactory is not null) return ValueTask.FromResult(EvidenceFactory(job));
        return ValueTask.FromResult(Evidence ?? new ReviewEvidenceReadResult(null, ReviewErrorCodes.EvidenceUnavailable));
    }

    public ValueTask<ReviewModelResult> ReviewAsync(ReviewInputSnapshot snapshot, string attemptId,
        CancellationToken ct = default)
    {
        lock (_gate) Executions++;
        DuringExecution?.Invoke();
        if (ExecutionFailure is not null) throw ExecutionFailure;
        return ValueTask.FromResult(Model is null
            ? throw new InvalidOperationException("No model result configured.")
            : Model(snapshot));
    }

    public ValueTask PublishCompletedAsync(ReviewCompletedEvent completed, CancellationToken ct = default)
    {
        lock (_gate) _events.Add(completed);
        return ValueTask.CompletedTask;
    }

    private ReviewJobView Require(string jobId) => _jobs.TryGetValue(jobId, out var view)
        ? view
        : throw new ReviewException(ReviewErrorCodes.NotFound, "The review job is missing.");

    private static bool RevisionMismatch(ReviewJobView view, long expectedRevision) => view.Revision != expectedRevision;

    private static string Fingerprint(ReviewLogicalKey key) => string.Join('|', key.WorkspaceId, key.ParentWorkId,
        key.GoalRunId, key.ActivationEpoch, key.IterationNo, key.ObjectiveVersion, key.AcceptanceContractVersion,
        key.ArtifactSetHash, key.ReviewPolicyVersion);
}
