using System.Security.Cryptography;
using System.Text;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// PA-04 auto-submission input: the frozen Goal iteration identity needed to create exactly one required review.
/// </summary>
public sealed record GoalReviewSubmissionRequest(string GoalRunId, string WorkspaceId, string? TaskId,
    long ActivationEpoch, int IterationNo, int ObjectiveVersion, string AcceptanceContractVersion,
    string ArtifactSetHash, string ReviewPolicyVersion, string ActorId, string BudgetAccountId,
    DateTimeOffset Deadline)
{
    public bool IsValid => Bounded(GoalRunId) && Bounded(WorkspaceId) && (TaskId is null || Bounded(TaskId))
        && ActivationEpoch >= 0 && IterationNo >= 0 && ObjectiveVersion >= 1 && Bounded(AcceptanceContractVersion)
        && Bounded(ArtifactSetHash) && Bounded(ReviewPolicyVersion) && Bounded(ActorId) && Bounded(BudgetAccountId)
        && Deadline > DateTimeOffset.UtcNow && Deadline <= DateTimeOffset.UtcNow.AddDays(1);

    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record GoalReviewSubmissionResult(string JobId, string ReviewId, bool Created);

/// <summary>
/// PA-04 → PA-03 seam: a Goal iteration that reaches its completion candidate submits exactly one required review job
/// with a deterministic job/operation id (re-submission is idempotent), the trusted actor and the Goal root budget.
/// It never changes Goal state; the completion gate consumes the resulting review fact.
/// </summary>
public sealed class GoalReviewSubmissionService(ReviewJobStore jobs, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GoalReviewSubmissionResult> SubmitAsync(GoalReviewSubmissionRequest request,
        CancellationToken ct = default)
    {
        if (request is null || !request.IsValid)
            throw new ReviewException(ReviewErrorCodes.RequestInvalid,
                "A bounded Goal review submission is required.");
        var key = new ReviewLogicalKey(request.WorkspaceId, request.TaskId, request.GoalRunId,
            request.ActivationEpoch, request.IterationNo, request.ObjectiveVersion.ToString(),
            request.AcceptanceContractVersion, request.ArtifactSetHash, request.ReviewPolicyVersion);
        if (!key.IsValid)
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "The Goal review key is invalid.");
        var identity = StableId("goal-review", request.GoalRunId, request.ActivationEpoch.ToString(),
            request.IterationNo.ToString(), request.ObjectiveVersion.ToString(), request.AcceptanceContractVersion,
            request.ArtifactSetHash, request.ReviewPolicyVersion);
        var record = new ReviewJobRecord("job-" + identity, "review-" + identity, key, Required: true,
            request.BudgetAccountId, request.Deadline, _clock.GetUtcNow(), AcceptedSequence: 0,
            ActorId: request.ActorId);
        var created = await jobs.CreateOrGetAsync(record, "goal-review:" + identity, ct).ConfigureAwait(false);
        return new(created.Job.JobId, created.Job.ReviewId, created.Created);
    }

    private static string StableId(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))
            .ToLowerInvariant()[..40];
}
