using System.Collections.Immutable;
using PuddingReviews.Contracts;

namespace PuddingReviews;

/// <summary>
/// PA-03 result fence. A model "Accept" is advice only: missing/unknown evidence, unknown criteria, version
/// mismatch, truncated or incomplete evidence and empty required criteria all downgrade the result to
/// Inconclusive. Caller-supplied strings can never earn CheckVerified, and Satisfied requires every criterion
/// to be Supported by known evidence.
/// </summary>
public sealed class ReviewResultValidator
{
    public ReviewOutcome Validate(ReviewInputSnapshot snapshot, bool required, ReviewModelResult? result)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var issue = FirstIssue(snapshot, required, result);
        if (issue is not null)
            return new(ReviewFindingValue.Inconclusive, ReviewEvidenceLevel.ModelAssessed,
                NormalizeCriteria(snapshot, result), issue);

        var criteria = NormalizeCriteria(snapshot, result);
        var notSupported = criteria.Any(c => c.Outcome == CriterionOutcomeValue.NotSupported);
        var finding = result!.Finding switch
        {
            ReviewFindingValue.Satisfied when notSupported => ReviewFindingValue.NeedsWork,
            ReviewFindingValue.Satisfied => ReviewFindingValue.Satisfied,
            ReviewFindingValue.NeedsWork or ReviewFindingValue.NeedsUser => result.Finding,
            ReviewFindingValue.Inconclusive => ReviewFindingValue.Inconclusive,
            _ => ReviewFindingValue.Inconclusive,
        };
        var reason = notSupported ? "review.criteria_not_supported" : result.ReasonCode;
        return new(finding, LevelOf(snapshot, criteria), criteria, reason);
    }

    private static string? FirstIssue(ReviewInputSnapshot snapshot, bool required, ReviewModelResult? result)
    {
        if (result is null) return ReviewErrorCodes.RequestInvalid;
        if (!ReviewInputValidator.KeyEquals(snapshot.Key, new ReviewLogicalKey(snapshot.Key.WorkspaceId,
                snapshot.Key.ParentWorkId, snapshot.Key.GoalRunId, snapshot.Key.ActivationEpoch,
                snapshot.Key.IterationNo, result.ObjectiveVersion, result.AcceptanceContractVersion,
                result.ArtifactSetHash, snapshot.Key.ReviewPolicyVersion)))
            return ReviewErrorCodes.VersionMismatch;
        if (!string.Equals(result.PromptVersion, snapshot.PromptVersion, StringComparison.Ordinal))
            return ReviewErrorCodes.VersionMismatch;
        if (required && snapshot.Criteria.IsEmpty) return ReviewErrorCodes.CriteriaMissing;
        if (!snapshot.EvidenceComplete) return ReviewErrorCodes.EvidenceIncomplete;
        if (result.Criteria.IsDefault) return ReviewErrorCodes.RequestInvalid;

        var criteriaById = snapshot.Criteria.ToDictionary(c => c.CriterionId, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in result.Criteria)
        {
            if (!criteriaById.ContainsKey(outcome.CriterionId)) return ReviewErrorCodes.UnknownCriterion;
            covered.Add(outcome.CriterionId);
            foreach (var evidenceId in outcome.EvidenceIds)
            {
                var evidence = snapshot.FindEvidence(evidenceId);
                if (evidence is null) return ReviewErrorCodes.UnknownEvidence;
                if (evidence.Truncated) return ReviewErrorCodes.EvidenceIncomplete;
            }
        }
        // A required review must cover every criterion; an uncovered criterion means the model cannot claim Satisfied.
        if (required && covered.Count != criteriaById.Count) return ReviewErrorCodes.UnknownCriterion;
        return null;
    }

    private static ImmutableArray<ReviewCriterionOutcome> NormalizeCriteria(ReviewInputSnapshot snapshot,
        ReviewModelResult? result)
    {
        if (result is null || result.Criteria.IsDefault) return [];
        var known = snapshot.Criteria.Select(c => c.CriterionId).ToHashSet(StringComparer.Ordinal);
        var evidenceIds = snapshot.Evidence.Select(e => e.EvidenceId).ToHashSet(StringComparer.Ordinal);
        return result.Criteria
            .Where(c => known.Contains(c.CriterionId))
            .Select(c => c with
            {
                EvidenceIds = c.EvidenceIds.IsDefault
                    ? ImmutableArray<string>.Empty
                    : [.. c.EvidenceIds.Where(evidenceIds.Contains)],
            })
            .ToImmutableArray();
    }

    private static ReviewEvidenceLevel LevelOf(ReviewInputSnapshot snapshot, ImmutableArray<ReviewCriterionOutcome> criteria)
    {
        var referenced = criteria.SelectMany(c => c.EvidenceIds).Distinct(StringComparer.Ordinal)
            .Select(snapshot.FindEvidence).Where(e => e is not null).Cast<ReviewEvidenceItem>().ToArray();
        if (referenced.Length == 0) return ReviewEvidenceLevel.ModelAssessed;
        if (referenced.Any(e => e.CallerSupplied)) return ReviewEvidenceLevel.ModelAssessed;
        if (referenced.All(e => e.Kind == EvidenceKind.CheckReport && !string.IsNullOrWhiteSpace(e.CheckExitCode))
            && snapshot.EvidenceComplete)
            return ReviewEvidenceLevel.CheckVerified;
        return ReviewEvidenceLevel.Observed;
    }
}
