using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>
/// PA-03 review-job facade. Submit only accepts a job; findings are advice and never a Goal terminal state.
/// The client forwards requests verbatim and never retries or synthesizes a review id.
/// </summary>
public sealed class ReviewJobsClient(IReviewJobsApi api)
{
    public Task<ReviewJobView> SubmitAsync(SubmitReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.SubmitAsync(request, ct);
    }

    public Task<ReviewJobView?> GetAsync(ReviewJobRef job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return api.GetAsync(job, ct);
    }

    public Task<ReviewJobView> CancelAsync(CancelReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.CancelAsync(request, ct);
    }
}
