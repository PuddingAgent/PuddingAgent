using PuddingGoalPolicy.Contracts;

namespace PuddingPlatform.Services.Reviews;

public sealed record GoalReviewFact(ReviewFact Review, bool Stale, string? ReasonCode);

/// <summary>
/// PA-04 S5 seam: maps the latest committed review projection to the pure Goal policy's review fact. Only a
/// Satisfied finding with CheckVerified evidence can be Satisfied; a version/artifact mismatch is Stale; an unknown
/// evidence level or inconclusive finding maps to NeedsUser/Unknown so a Goal cannot complete on model advice alone.
/// No Goal state is written here.
/// </summary>
public static class GoalReviewFactMapper
{
    public static GoalReviewFact Map(ReviewOutcomeProjection? latest, string expectedArtifactSetHash,
        string expectedAcceptanceContractVersion, bool reviewRequired)
    {
        if (latest is null)
            return reviewRequired
                ? new(ReviewFact.Missing, Stale: false, GoalPolicyErrorCodes.ReviewMissing)
                : new(ReviewFact.NotRequired, Stale: false, null);
        if (!string.Equals(latest.ArtifactSetHash, expectedArtifactSetHash, StringComparison.Ordinal)
            || !string.Equals(latest.AcceptanceContractVersion ?? "", expectedAcceptanceContractVersion,
                StringComparison.Ordinal))
            return new(ReviewFact.Stale, Stale: true, GoalPolicyErrorCodes.ReviewStale);
        return latest.Finding switch
        {
            "Satisfied" when string.Equals(latest.Level, "CheckVerified", StringComparison.Ordinal) =>
                new(ReviewFact.Satisfied, Stale: false, null),
            "Satisfied" => new(ReviewFact.NeedsUser, Stale: false, GoalPolicyErrorCodes.ReviewNeedsUser),
            "NeedsWork" => new(ReviewFact.NeedsWork, Stale: false, GoalPolicyErrorCodes.ReviewNeedsWork),
            "NeedsUser" => new(ReviewFact.NeedsUser, Stale: false, GoalPolicyErrorCodes.ReviewNeedsUser),
            "Inconclusive" => new(ReviewFact.Unknown, Stale: false, GoalPolicyErrorCodes.ReviewUnknown),
            _ => new(ReviewFact.Unknown, Stale: false, GoalPolicyErrorCodes.ReviewUnknown),
        };
    }
}
