using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// I5 completion gate lookup: newest accepted review for the exact Goal objective version. A newer review for another
/// objective version must not shadow the review that belongs to the current completion candidate.
/// </summary>
public interface IReviewJobByGoalObjectiveReader
{
    Task<ReviewJobView?> GetLatestByGoalObjectiveAsync(string goalRunId, string objectiveVersion,
        CancellationToken ct = default);
}
