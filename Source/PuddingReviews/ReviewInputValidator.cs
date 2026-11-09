using PuddingReviews.Contracts;

namespace PuddingReviews;

public sealed record ReviewInputValidation(bool Valid, string? ReasonCode);

/// <summary>
/// PA-03 input fence: the frozen snapshot must belong to the job's logical key and versions, stay inside the
/// job deadline, and may not be silently replaced by a newer artifact set. Incomplete evidence is recorded for
/// the result validator (it must end Inconclusive), not silently repaired here.
/// </summary>
public sealed class ReviewInputValidator
{
    public ReviewInputValidation Validate(ReviewJobView job, ReviewInputSnapshot snapshot)
    {
        if (job is null || snapshot is null || !snapshot.IsValid)
            return new(false, ReviewErrorCodes.RequestInvalid);
        if (!string.Equals(snapshot.ReviewId, job.ReviewId, StringComparison.Ordinal))
            return new(false, ReviewErrorCodes.VersionMismatch);
        if (!KeyEquals(job.Key, snapshot.Key))
            return new(false, ReviewErrorCodes.VersionMismatch);
        if (snapshot.Deadline > job.Deadline)
            return new(false, ReviewErrorCodes.RequestInvalid);
        return new(true, null);
    }

    internal static bool KeyEquals(ReviewLogicalKey left, ReviewLogicalKey right) =>
        string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(left.ParentWorkId, right.ParentWorkId, StringComparison.Ordinal)
        && string.Equals(left.GoalRunId, right.GoalRunId, StringComparison.Ordinal)
        && left.ActivationEpoch == right.ActivationEpoch
        && left.IterationNo == right.IterationNo
        && string.Equals(left.ObjectiveVersion, right.ObjectiveVersion, StringComparison.Ordinal)
        && string.Equals(left.AcceptanceContractVersion, right.AcceptanceContractVersion, StringComparison.Ordinal)
        && string.Equals(left.ArtifactSetHash, right.ArtifactSetHash, StringComparison.Ordinal)
        && string.Equals(left.ReviewPolicyVersion, right.ReviewPolicyVersion, StringComparison.Ordinal);
}
