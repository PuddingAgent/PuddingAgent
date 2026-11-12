using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class ReviewJobsClientTests
{
    [TestMethod]
    public async Task ForwardsSubmitGetAndCancelVerbatim_WithoutRewritingTheFinding()
    {
        var api = new Fake();
        var client = new ReviewJobsClient(api);
        var request = new SubmitReviewRequest("op-1", "ws-1", "work-1", null, 3, 2, "objective-v1",
            "acceptance-v1", "artifact-hash", "policy-v1");

        var submitted = await client.SubmitAsync(request);
        Assert.AreEqual(request, api.Submit);
        Assert.AreEqual("Pending", submitted.State);
        Assert.AreEqual("inconclusive", submitted.Finding, "The client must not rewrite the advice finding.");

        var view = await client.GetAsync(new("job-1", "review-1"));
        Assert.AreEqual("Completed", view!.State);
        Assert.AreEqual("review-1", view.ReviewId);

        var cancel = await client.CancelAsync(new("op-2", new("job-1", "review-1"), 2));
        Assert.AreEqual("Cancelled", cancel.State);
        Assert.AreEqual(1, api.Calls);
    }

    [TestMethod]
    public async Task GetMissing_ReturnsNull_AndTypedErrorsAreNotWrapped()
    {
        var api = new Fake { Missing = true };
        var client = new ReviewJobsClient(api);
        Assert.IsNull(await client.GetAsync(new("job-missing", "review-missing")));

        api.Failure = new ReviewJobException("review.revision_conflict", "stale");
        var failure = await Assert.ThrowsExactlyAsync<ReviewJobException>(() =>
            client.CancelAsync(new("op-2", new("job-1", "review-1"), 9)));
        Assert.AreEqual("review.revision_conflict", failure.Code);
    }

    private sealed class Fake : IReviewJobsApi
    {
        public SubmitReviewRequest? Submit;
        public bool Missing;
        public Exception? Failure;
        public int Calls;

        public Task<ReviewJobView> SubmitAsync(SubmitReviewRequest request, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<ReviewJobView>(Failure);
            Submit = request;
            Calls++;
            return Task.FromResult(View("Pending", "inconclusive"));
        }

        public Task<ReviewJobView?> GetAsync(ReviewJobRef job, CancellationToken ct = default)
            => Task.FromResult<ReviewJobView?>(Missing ? null : View("Completed", "satisfied"));

        public Task<ReviewJobView> CancelAsync(CancelReviewRequest request, CancellationToken ct = default)
            => Failure is not null
                ? Task.FromException<ReviewJobView>(Failure)
                : Task.FromResult(View("Cancelled", "inconclusive"));

        private static ReviewJobView View(string state, string finding) => new("job-1", "review-1", state,
            finding, "ModelAssessed", ImmutableArray<ReviewCriterionResult>.Empty, null, 3, 1, DateTimeOffset.UtcNow);
    }
}
