using Pudding.Api.Contracts;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;
using PuddingModelEvaluation;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>Host-owned live model acquisition (provider gateway + lease). Kept outside the review component.</summary>
public interface IReviewModelLeaseSource
{
    ValueTask<ILeasedModel> AcquireAsync(ReviewModelBinding binding, CancellationToken ct = default);
}

/// <summary>
/// PA-03 live binder: resolves the trusted route (actor + Review purpose), acquires a host model lease, and returns the
/// public evaluation API backed by the durable budget pool so the model attempt has exactly one persistent reservation.
/// </summary>
public sealed class ReviewEvaluationBinder(ReviewModelRouter router, IReviewModelLeaseSource leases,
    ModelAttemptBudgetGate gate) : IReviewEvaluationBinder
{
    public async ValueTask<IModelEvaluationApi> BindAsync(ReviewInputSnapshot snapshot, string attemptId,
        CancellationToken ct = default)
    {
        if (snapshot is null || !snapshot.IsValid || string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128)
            throw new PuddingReviews.Contracts.ReviewException(ReviewErrorCodes.RequestInvalid,
                "A valid review snapshot and attempt id are required.");
        try
        {
            var binding = await router.ResolveAsync(snapshot, ct).ConfigureAwait(false);
            var identity = new ExecutionResourceIdentity(binding.Identity.WorkspaceId, binding.Identity.AgentId,
                binding.Identity.SessionId, attemptId);
            var scope = new ExecutionResourceScope(identity, new ExecutionResourceLimits(), snapshot.Deadline);
            var leased = await leases.AcquireAsync(binding, ct).ConfigureAwait(false);
            var pool = new DurableModelResourcePool(gate, binding.BudgetAccount.Value, [], binding.Identity,
                binding.CostUnit, binding.PriceVersion, "review");
            return new ModelEvaluationService(pool).Bind(scope, leased, canList: false, canClassify: false,
                canReview: true);
        }
        catch (PuddingReviews.Contracts.ReviewException)
        {
            throw;
        }
        catch (ExecutionResourceException ex)
        {
            throw new PuddingReviews.Contracts.ReviewException(ReviewErrorCodes.ExecutorUnknown, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PuddingReviews.Contracts.ReviewException(ReviewErrorCodes.ExecutorUnknown,
                "The review model could not be bound.");
        }
    }
}
