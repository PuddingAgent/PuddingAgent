using PuddingCode.Goals;
using PuddingGoalPolicy.Contracts;
using PuddingPlatform.Services.Reviews;
using IReviewJobByGoalObjectiveReader = PuddingPlatform.Services.Reviews.IReviewJobByGoalObjectiveReader;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatform.Services.Goals;

/// <summary>Injectable completion gate: a Goal with an attempted review may only complete when that review is satisfied.</summary>
public interface IGoalReviewCompletionGate
{
    ValueTask<ReviewFact> EvaluateAsync(GoalEvidenceCapsule capsule, CancellationToken ct = default);
}

/// <summary>
/// PA-04 Goal completion gate: when a review job was attempted for this Goal (no job means review is not required for
/// this Goal and the existing evidence rules stand), completion requires the latest job to be Completed with a satisfied,
/// check-verified outcome on the same objective version. Pending/unknown/needs-work/stale results never complete.
/// </summary>
/// <summary>Opt-in policy: whether this Goal candidate requires a review at all. Default is "not required" until the
/// product chooses an explicit mode; enabling it elsewhere must be a deliberate decision.</summary>
public interface IGoalReviewRequirementPolicy
{
    ValueTask<bool> IsRequiredAsync(GoalEvidenceCapsule capsule, CancellationToken ct = default);
}

public sealed class NoGoalReviewRequirementPolicy : IGoalReviewRequirementPolicy
{
    public ValueTask<bool> IsRequiredAsync(GoalEvidenceCapsule capsule, CancellationToken ct = default)
        => ValueTask.FromResult(false);
}

public sealed class GoalReviewCompletionGate(IReviewJobByGoalObjectiveReader jobs,
    IGoalReviewRequirementPolicy? requirement = null) : IGoalReviewCompletionGate
{
    public async ValueTask<ReviewFact> EvaluateAsync(GoalEvidenceCapsule capsule, CancellationToken ct = default)
    {
        if (capsule is null || string.IsNullOrWhiteSpace(capsule.GoalRunId)) return ReviewFact.NotRequired;
        var latest = await jobs.GetLatestByGoalObjectiveAsync(capsule.GoalRunId,
            capsule.ObjectiveVersion.ToString(), ct).ConfigureAwait(false);
        if (latest is null)
            return requirement is not null && await requirement.IsRequiredAsync(capsule, ct).ConfigureAwait(false)
                ? ReviewFact.Missing
                : ReviewFact.NotRequired;
        return latest.State switch
        {
            PuddingReviews.Contracts.ReviewJobState.Pending => ReviewFact.Pending,
            PuddingReviews.Contracts.ReviewJobState.Waiting => ReviewFact.Pending,
            PuddingReviews.Contracts.ReviewJobState.Running => ReviewFact.Pending,
            PuddingReviews.Contracts.ReviewJobState.Failed => ReviewFact.Unknown,
            PuddingReviews.Contracts.ReviewJobState.Cancelled => ReviewFact.Unknown,
            PuddingReviews.Contracts.ReviewJobState.Completed => Satisfied(latest),
            _ => ReviewFact.Unknown,
        };
    }

    private static ReviewFact Satisfied(ReviewJobView job)
    {
        if (job.Outcome is not { } outcome) return ReviewFact.Unknown;
        return outcome.Finding switch
        {
            PuddingReviews.Contracts.ReviewFindingValue.Satisfied => outcome.Level
                == PuddingReviews.Contracts.ReviewEvidenceLevel.CheckVerified
                    ? ReviewFact.Satisfied
                    : ReviewFact.NeedsUser,
            PuddingReviews.Contracts.ReviewFindingValue.NeedsWork => ReviewFact.NeedsWork,
            _ => ReviewFact.NeedsUser,
        };
    }
}
