using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// Host-owned provider execution for one review model binding (adapter over the product model gateway). It receives the
/// frozen descriptor and the bounded request; the durable budget pool performs reservation/settlement around it.
/// </summary>
public interface IReviewModelProvider
{
    ValueTask<ModelCompletion> CompleteAsync(ReviewModelBinding binding, ModelResourceRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// PA-03 lease source: converts the trusted binding into a host leased model with the frozen catalog limits/prices.
/// The provider adapter is the only place that knows the product gateway; reviews never touch credentials.
/// </summary>
public sealed class ReviewModelLeaseSource(IReviewModelProvider provider) : IReviewModelLeaseSource
{
    public ValueTask<ILeasedModel> AcquireAsync(ReviewModelBinding binding, CancellationToken ct = default)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.ModelId) || string.IsNullOrWhiteSpace(binding.ModelRevision)
            || binding.MaxInputTokens is < 1 or > 10_000_000 || binding.MaxOutputTokens is < 1 or > 1_000_000)
            throw new ExecutionResourceException("model.configuration_invalid",
                "A frozen review model binding is required.");
        return ValueTask.FromResult<ILeasedModel>(new Lease(provider, binding));
    }

    private sealed class Lease(IReviewModelProvider provider, ReviewModelBinding binding) : ILeasedModel
    {
        public ModelResourceDescriptor Descriptor { get; } = new(
            "review:" + binding.ModelId + ":" + binding.ModelRevision, binding.ModelRevision, binding.ModelId,
            binding.MaxInputTokens, binding.MaxOutputTokens, MaxConcurrentCalls: 2,
            new ModelPrice(binding.InputPerMillion, binding.OutputPerMillion, binding.CacheHitPerMillion));

        public async Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(dispatch);
            dispatch();
            return await provider.CompleteAsync(binding, request, ct).ConfigureAwait(false);
        }
    }
}
