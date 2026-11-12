using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 review executor adapter: acceptance receipt mapping (accept/reject/abstain), degraded receipt is a
/// typed executor failure, version/fingerprint provenance is preserved, and permission denial surfaces as a
/// typed executor failure rather than a model finding.
/// </summary>
[TestClass]
public sealed class ReviewExecutorTests
{
    [TestMethod]
    public async Task MapsAcceptRejectAndAbstainReceipts()
    {
        foreach (var (decision, finding) in new[]
                 {
                     (AcceptanceDecision.Accept, ReviewFindingValue.Satisfied),
                     (AcceptanceDecision.Reject, ReviewFindingValue.NeedsWork),
                     (AcceptanceDecision.Abstain, ReviewFindingValue.Inconclusive),
                 })
        {
            var api = new FakeApi(Receipt(decision));
            var result = await new ReviewExecutor(new FakeBinder(api)).ReviewAsync(Snapshot(), "attempt-1");
            Assert.AreEqual(finding, result.Finding, decision.ToString());
            Assert.AreEqual(1, result.Criteria.Length);
            Assert.AreEqual("model-a", result.ModelId);
            Assert.AreEqual("rev-1", result.ModelRevision);
            Assert.AreEqual("fingerprint", result.ResponseFingerprint);
            Assert.AreEqual("objective-v1", result.ObjectiveVersion);
            Assert.AreEqual("acceptance-v1", result.AcceptanceContractVersion);
            Assert.AreEqual("artifact-hash", result.ArtifactSetHash);
            Assert.AreEqual("attempt-1", api.LastRequest!.Options.CallId);
        }
    }

    [TestMethod]
    public async Task DegradedReceipt_IsATypedExecutorFailure_NotAPass()
    {
        var api = new FakeApi(Receipt(AcceptanceDecision.Abstain) with
        {
            Availability = EvaluationAvailability.Degraded,
            ReasonCode = "model.response_invalid",
        });
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await new ReviewExecutor(new FakeBinder(api)).ReviewAsync(Snapshot(), "attempt-1"));
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, failure.Code);
    }

    [TestMethod]
    public async Task PermissionDenialAndBindingFailure_AreTyped()
    {
        var denied = new ReviewExecutor(new FakeBinder(new FakeApi(Receipt(AcceptanceDecision.Accept)),
            bindFailure: new ModelEvaluationException("permission.denied", "no model review grant")));
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await denied.ReviewAsync(Snapshot(), "attempt-1"));
        Assert.AreEqual(ReviewErrorCodes.ExecutorFailed, failure.Code);

        var unavailable = new ReviewExecutor(new FakeBinder(new FakeApi(Receipt(AcceptanceDecision.Accept)),
            bindFailure: new ModelEvaluationException("model.unavailable", "registry down")));
        var unknown = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await unavailable.ReviewAsync(Snapshot(), "attempt-1"));
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, unknown.Code);
    }

    [TestMethod]
    public async Task RequestCarriesAllEvidenceAndVersions()
    {
        var api = new FakeApi(Receipt(AcceptanceDecision.Accept));
        await new ReviewExecutor(new FakeBinder(api)).ReviewAsync(Snapshot(), "attempt-9");
        var input = api.LastRequest!.Input;
        Assert.AreEqual("Build both files.", input.Task);
        Assert.AreEqual("criterion-1", input.Criteria[0].Id);
        Assert.IsTrue(input.Evidence[0].Text.Contains("sha256=sha-check"));
        Assert.IsTrue(api.LastRequest.Options.MaxOutputTokens > 0);
    }

    private static AcceptanceReceipt Receipt(AcceptanceDecision decision) => new(
        EvaluationAvailability.Valid, decision,
        [new("criterion-1", decision == AcceptanceDecision.Reject ? CriterionOutcome.NotSupported
            : CriterionOutcome.Supported, ["evidence-1"])],
        0.9, "checked", null,
        new("call-1", "lease-1", new("ws-1", "agent-1", "session-1"), "parent-1", "delivery.acceptance", 1,
            "model-a", "rev-1", "input-fingerprint", "fingerprint",
            new EvaluationModelUsage(10, 20, 0, 0.01m, "usd")));

    private static ReviewInputSnapshot Snapshot() => new("review-1",
        new("ws-1", null, "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        "Build both files.", [new("criterion-1", "Both files exist.")],
        [new("evidence-1", EvidenceKind.CheckReport, "check://build", "check://build/1", "sha-check",
            DateTimeOffset.UtcNow, "controlled-check", Truncated: false, CheckExitCode: "0")],
        EvidenceComplete: true, [], "prompt-v1", "policy-v1", "budget-1", DateTimeOffset.UtcNow.AddMinutes(10));

    private sealed class FakeBinder(IModelEvaluationApi api, ModelEvaluationException? bindFailure = null)
        : IReviewEvaluationBinder
    {
        public ValueTask<IModelEvaluationApi> BindAsync(ReviewInputSnapshot snapshot, string attemptId,
            CancellationToken ct = default)
            => bindFailure is not null
                ? ValueTask.FromException<IModelEvaluationApi>(bindFailure)
                : ValueTask.FromResult(api);
    }

    private sealed class FakeApi(AcceptanceReceipt receipt, ModelEvaluationException? bindFailure = null)
        : IModelEvaluationApi
    {
        public AcceptanceRequest? LastRequest { get; private set; }

        public Task<OperatorCatalog> ListAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ClassificationReceipt> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AcceptanceReceipt> ReviewAsync(AcceptanceRequest request, CancellationToken ct = default)
        {
            if (bindFailure is not null) throw bindFailure;
            LastRequest = request;
            return Task.FromResult(receipt);
        }
    }
}
