using PuddingReviews.Contracts;

namespace PuddingReviews;

public sealed record ReviewRunResult(ReviewJobView Job, bool Executed);

/// <summary>
/// PA-03 coordinator: persistent acceptance, owner-fenced claim, committed-evidence read, bound model attempt,
/// result validation and a completion fact through the outbox. It never writes Goal state; a completed review is
/// advice, and a required review that fails stays Waiting/Unknown rather than passing by default.
/// </summary>
public sealed class ReviewJobCoordinator
{
    private readonly IReviewJobStore _store;
    private readonly IReviewEvidenceReader _reader;
    private readonly IReviewExecutor _executor;
    private readonly ReviewInputValidator _inputValidator;
    private readonly ReviewResultValidator _resultValidator;
    private readonly TimeProvider _clock;

    public ReviewJobCoordinator(IReviewJobStore store, IReviewEvidenceReader reader, IReviewExecutor executor,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _inputValidator = new ReviewInputValidator();
        _resultValidator = new ReviewResultValidator();
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<ReviewCreateResult> SubmitAsync(ReviewJobRecord candidate, string operationId,
        CancellationToken ct = default)
    {
        if (candidate is null || !candidate.IsValid) throw Error(ReviewErrorCodes.RequestInvalid, "A valid review job record is required.");
        if (!Valid(operationId)) throw Error(ReviewErrorCodes.RequestInvalid, "A bounded operation id is required.");
        var now = _clock.GetUtcNow();
        if (candidate.Deadline <= now || candidate.Deadline > now.AddDays(1))
            throw Error(ReviewErrorCodes.RequestInvalid, "The review deadline is out of bounds.");
        return await _store.CreateOrGetAsync(candidate, operationId, ct).ConfigureAwait(false);
    }

    public ValueTask<ReviewJobView?> GetAsync(string reviewId, CancellationToken ct = default)
    {
        if (!Valid(reviewId)) throw Error(ReviewErrorCodes.RequestInvalid, "A bounded review id is required.");
        return _store.GetByReviewAsync(reviewId, ct);
    }

    public async ValueTask<ReviewJobView> CancelAsync(string jobId, long expectedRevision, string reasonCode,
        CancellationToken ct = default)
    {
        if (!Valid(jobId) || !Valid(reasonCode) || expectedRevision < 0)
            throw Error(ReviewErrorCodes.RequestInvalid, "Bounded cancellation input is required.");
        return await _store.CancelAsync(jobId, expectedRevision, reasonCode, ct).ConfigureAwait(false);
    }

    /// <summary>One worker attempt. A terminal job is never re-executed; a stale owner cannot claim or complete.</summary>
    public async ValueTask<ReviewRunResult> RunOnceAsync(string jobId, long ownerEpoch, TimeSpan lease,
        CancellationToken ct = default)
    {
        if (!Valid(jobId) || ownerEpoch < 1) throw Error(ReviewErrorCodes.RequestInvalid, "A bounded job id and owner epoch are required.");
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(1))
            throw Error(ReviewErrorCodes.RequestInvalid, "The lease is out of bounds.");
        var current = await _store.GetAsync(jobId, ct).ConfigureAwait(false)
            ?? throw Error(ReviewErrorCodes.NotFound, "The review job is missing.");
        if (current.State is ReviewJobState.Completed or ReviewJobState.Failed or ReviewJobState.Cancelled)
            return new(current, Executed: false);
        if (ownerEpoch < current.OwnerEpoch) throw Error(ReviewErrorCodes.OwnerStale, "A stale owner cannot run this review.");
        var claimed = await _store.ClaimAsync(jobId, ownerEpoch, _clock.GetUtcNow().Add(lease), current.Revision, ct)
            .ConfigureAwait(false);

        ReviewEvidenceReadResult read;
        try
        {
            read = await _reader.ReadAsync(claimed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ReviewException ex)
        {
            var state = ex.Code == ReviewErrorCodes.SnapshotRequired ? ReviewJobState.Waiting : ReviewJobState.Unknown;
            return new(await FailAsync(claimed, ownerEpoch, state, ex.Code, ct).ConfigureAwait(false), Executed: true);
        }
        catch (Exception)
        {
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Unknown, ReviewErrorCodes.ExecutorUnknown, ct)
                .ConfigureAwait(false), Executed: true);
        }
        if (read.Snapshot is null)
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Waiting,
                read.ReasonCode ?? ReviewErrorCodes.EvidenceUnavailable, ct).ConfigureAwait(false), Executed: true);

        var input = _inputValidator.Validate(claimed, read.Snapshot);
        if (!input.Valid)
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Unknown, input.ReasonCode!, ct)
                .ConfigureAwait(false), Executed: true);

        ReviewModelResult model;
        try
        {
            model = await _executor.ReviewAsync(read.Snapshot, claimed.JobId + ":" + ownerEpoch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The provider attempt may have charged; the job stays unknown instead of passing.
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Unknown, ReviewErrorCodes.ExecutorUnknown,
                CancellationToken.None).ConfigureAwait(false), Executed: true);
        }
        catch (ReviewException ex)
        {
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Unknown, ex.Code, CancellationToken.None)
                .ConfigureAwait(false), Executed: true);
        }
        catch (Exception)
        {
            return new(await FailAsync(claimed, ownerEpoch, ReviewJobState.Unknown, ReviewErrorCodes.ExecutorUnknown,
                CancellationToken.None).ConfigureAwait(false), Executed: true);
        }

        var outcome = _resultValidator.Validate(read.Snapshot, claimed.Required, model);
        // The completion event identity is stable per job revision so a retried completion cannot publish twice.
        var completedEvent = new ReviewCompletedEvent(StableCompletionEventId(claimed.JobId, claimed.Revision),
            claimed.ReviewId, claimed.JobId, claimed.Key, outcome.Finding, outcome.Level, claimed.Revision + 1,
            _clock.GetUtcNow());
        var completed = await _store.CompleteAsync(claimed.JobId, ownerEpoch, claimed.Revision, outcome,
            completedEvent, ct).ConfigureAwait(false);
        return new(completed, Executed: true);
    }

    private ValueTask<ReviewJobView> FailAsync(ReviewJobView claimed, long ownerEpoch, ReviewJobState state,
        string reasonCode, CancellationToken ct)
        => _store.FailAsync(claimed.JobId, ownerEpoch, claimed.Revision, state, reasonCode, ct);

    private static string StableCompletionEventId(string jobId, long claimedRevision)
        => "review-completed-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(jobId + "|" + claimedRevision)))[..32];

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static ReviewException Error(string code, string message) => new(code, message);
}
