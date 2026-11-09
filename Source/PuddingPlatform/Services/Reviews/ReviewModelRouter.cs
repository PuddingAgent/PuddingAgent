using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingModels;
using PuddingModels.Contracts;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatform.Services.Reviews;

public sealed record ReviewModelBinding(string ModelId, string ModelRevision, string PriceVersion,
    string ProviderId, string CostUnit, bool InheritedFromPrimary, BudgetAccountId BudgetAccount, BudgetIdentity Identity,
    int MaxInputTokens = 128_000, int MaxOutputTokens = 8_192, decimal InputPerMillion = 0m,
    decimal CacheHitPerMillion = 0m, decimal OutputPerMillion = 0m, string Currency = "usd");

/// <summary>
/// PA-03 binder routing core: the Review purpose is resolved from the existing model-policy authority for the
/// host-bound actor; only an explicit InheritPrimary entry may borrow the Primary model. The catalog supplies the
/// frozen model revision/pricing version used for the budget claim. A missing actor or policy fails typed instead of
/// substituting the agent id or a default model.
/// </summary>
public sealed class ReviewModelRouter(IAgentModelPolicyStore policies, ExecutionBudgetStore budgets,
    IModelCatalogSource catalog)
{
    /// <summary>Binder-facing overload: the frozen snapshot already carries the budget account and trusted actor.</summary>
    public Task<ReviewModelBinding> ResolveAsync(ReviewInputSnapshot snapshot, CancellationToken ct = default)
        => ResolveCoreAsync(snapshot?.BudgetAccountId, snapshot?.ActorId, ct);
    public async Task<ReviewModelBinding> ResolveAsync(ReviewJobView job, CancellationToken ct = default)
        => await ResolveCoreAsync(job?.BudgetAccountId, job?.ActorId, ct).ConfigureAwait(false);

    private async Task<ReviewModelBinding> ResolveCoreAsync(string? budgetAccountId, string? actorId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(budgetAccountId))
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A review job with a budget account is required.");
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ReviewException(ReviewErrorCodes.ActorUnavailable,
                "The review job has no trusted actor identity; the model policy cannot be resolved.");
        var (identity, costUnit) = await budgets.GetAccountIdentityAsync(new(budgetAccountId), ct)
            .ConfigureAwait(false);
        var scope = new AgentProfileScope(identity.WorkspaceId, identity.AgentId, actorId);
        EffectiveAgentModelPolicy effective;
        try
        {
            effective = await new ModelPolicyResolver(policies).ResolveAsync(scope, ct).ConfigureAwait(false);
        }
        catch (ModelException ex)
        {
            throw new ReviewException(ex.Code == ModelErrorCodes.ConfigRevisionConflict
                ? ReviewErrorCodes.ExecutorFailed : ReviewErrorCodes.ExecutorUnknown, ex.Message);
        }
        var review = effective.Purposes.FirstOrDefault(p => string.Equals(p.Purpose, "Review", StringComparison.Ordinal));
        if (review is null || string.IsNullOrWhiteSpace(review.ModelId))
            throw new ReviewException(ModelErrorCodes.PolicyMissing,
                "The Review purpose has no configured model (only an explicit InheritPrimary may borrow Primary).");
        var capability = await catalog.GetAsync(review.ModelId, ct).ConfigureAwait(false);
        if (capability is null || string.IsNullOrWhiteSpace(capability.Revision))
            throw new ReviewException(ModelErrorCodes.CapabilityMissing,
                "The review model has no authorized catalog revision.");
        return new(review.ModelId, capability.Revision, capability.PricingRuleVersion,
            string.IsNullOrWhiteSpace(capability.Availability) ? "unknown" : capability.Availability, costUnit,
            string.Equals(review.Source, "inherited-primary", StringComparison.Ordinal),
            new BudgetAccountId(budgetAccountId), identity, capability.MaxContextTokens, capability.MaxOutputTokens,
            capability.InputPerMillion ?? 0m, capability.CacheHitPerMillion ?? 0m, capability.OutputPerMillion ?? 0m,
            capability.Currency ?? costUnit);
    }
}
