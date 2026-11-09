using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5: host binds a live, budget-scoped model evaluation API for one review attempt. The adapter never
/// receives model credentials or the runtime gateway; it only gets the already-authorized API facade.
/// </summary>
public interface IReviewEvaluationBinder
{
    ValueTask<IModelEvaluationApi> BindAsync(ReviewInputSnapshot snapshot, string attemptId, CancellationToken ct = default);
}

/// <summary>
/// PA-03 S5 review executor adapter: maps the frozen review snapshot to the public acceptance-review API and maps
/// the receipt back to the independent review result contract. A model abstention is a valid semantic result
/// (Inconclusive), a degraded/unknown receipt is a typed executor failure (never a silent pass), and every
/// result carries the frozen objective/contract/artifact versions plus provider provenance.
/// </summary>
public sealed class ReviewExecutor(IReviewEvaluationBinder binder) : IReviewExecutor
{
    public async ValueTask<ReviewModelResult> ReviewAsync(ReviewInputSnapshot snapshot, string attemptId,
        CancellationToken ct = default)
    {
        if (snapshot is null || !snapshot.IsValid || string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128)
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A valid review snapshot and attempt id are required.");
        IModelEvaluationApi api;
        try
        {
            api = await binder.BindAsync(snapshot, attemptId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModelEvaluationException ex)
        {
            throw new ReviewException(ex.Code == "permission.denied" ? ReviewErrorCodes.ExecutorFailed
                : ReviewErrorCodes.ExecutorUnknown, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ReviewException(ReviewErrorCodes.ExecutorUnknown, "The review model could not be bound.");
        }

        AcceptanceReceipt receipt;
        try
        {
            receipt = await api.ReviewAsync(BuildRequest(snapshot, attemptId), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModelEvaluationException ex)
        {
            throw new ReviewException(ReviewErrorCodes.ExecutorUnknown, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ReviewException(ReviewErrorCodes.ExecutorUnknown, "The review model call failed.");
        }
        if (receipt.Availability is EvaluationAvailability.Degraded or EvaluationAvailability.Unknown)
            throw new ReviewException(ReviewErrorCodes.ExecutorUnknown,
                receipt.ReasonCode ?? "The review model returned no usable result.");

        var finding = receipt.Decision switch
        {
            AcceptanceDecision.Accept => ReviewFindingValue.Satisfied,
            AcceptanceDecision.Reject => ReviewFindingValue.NeedsWork,
            _ => ReviewFindingValue.Inconclusive,
        };
        var criteria = receipt.Checks.IsDefault
            ? ImmutableArray<ReviewCriterionOutcome>.Empty
            : receipt.Checks.Select(c => new ReviewCriterionOutcome(c.CriterionId, c.Outcome switch
            {
                CriterionOutcome.Supported => CriterionOutcomeValue.Supported,
                CriterionOutcome.NotSupported => CriterionOutcomeValue.NotSupported,
                _ => CriterionOutcomeValue.Unknown,
            }, c.EvidenceIds)).ToImmutableArray();
        return new ReviewModelResult(finding, criteria, receipt.Confidence, receipt.Reason, receipt.ReasonCode,
            snapshot.Key.ObjectiveVersion, snapshot.Key.AcceptanceContractVersion, snapshot.Key.ArtifactSetHash,
            snapshot.PromptVersion, receipt.Provenance.ModelId, receipt.Provenance.ModelRevision,
            receipt.Provenance.ResponseFingerprint);
    }

    private static AcceptanceRequest BuildRequest(ReviewInputSnapshot snapshot, string attemptId)
    {
        var task = snapshot.Objective.Length <= 4000 ? snapshot.Objective : snapshot.Objective[..4000];
        var criteria = snapshot.Criteria.Select(c => new AcceptanceCriterion(c.CriterionId,
            c.Text.Length <= 2000 ? c.Text : c.Text[..2000])).ToImmutableArray();
        var evidence = snapshot.Evidence.Select(e => new AcceptanceEvidence(e.EvidenceId,
            $"kind={e.Kind}; source={e.Source}; reference={e.Reference}; sha256={e.ContentHash}; exit={e.CheckExitCode ?? "n/a"}; truncated={e.Truncated}"))
            .ToImmutableArray();
        return new(new AcceptanceInput(task, criteria, evidence),
            new ModelEvaluationOptions(attemptId, MaxOutputTokens: 2048, TimeoutMilliseconds: 60_000));
    }
}
