using System.Collections.Immutable;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingReviewsTests;

[TestClass]
public sealed class ReviewEvidenceTests
{
    [TestMethod]
    public async Task EmptyRequiredCriteria_IsInconclusive_NoVacuousPass()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var snapshot = ReviewFixtures.Snapshot(job, criteria: []);
        var outcome = new ReviewResultValidator().Validate(snapshot, required: true, ReviewFixtures.Result(snapshot));
        Assert.AreEqual(ReviewFindingValue.Inconclusive, outcome.Finding);
        Assert.AreEqual(ReviewErrorCodes.CriteriaMissing, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task IncompleteEvidence_IsInconclusive()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var snapshot = ReviewFixtures.Snapshot(job, evidenceComplete: false,
            missingReasons: [ReviewErrorCodes.EvidenceIncomplete]);
        var outcome = new ReviewResultValidator().Validate(snapshot, true, ReviewFixtures.Result(snapshot));
        Assert.AreEqual(ReviewFindingValue.Inconclusive, outcome.Finding);
        Assert.AreEqual(ReviewErrorCodes.EvidenceIncomplete, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task TruncatedEvidenceOrUnknownReferences_AreInconclusive()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var truncated = ReviewFixtures.DefaultEvidence();
        truncated[1] = truncated[1] with { Truncated = true };
        var truncatedSnapshot = ReviewFixtures.Snapshot(job, evidence: [.. truncated]);
        Assert.AreEqual(ReviewErrorCodes.EvidenceIncomplete,
            new ReviewResultValidator().Validate(truncatedSnapshot, true, ReviewFixtures.Result(truncatedSnapshot)).ReasonCode);

        var normal = ReviewFixtures.Snapshot(job);
        var unknownCriterion = ReviewFixtures.Result(normal,
            criteria: [new("criterion-9", CriterionOutcomeValue.Supported, ["evidence-1"])]);
        Assert.AreEqual(ReviewErrorCodes.UnknownCriterion,
            new ReviewResultValidator().Validate(normal, true, unknownCriterion).ReasonCode);

        var unknownEvidence = ReviewFixtures.Result(normal,
            criteria: [new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-unknown"])]);
        Assert.AreEqual(ReviewErrorCodes.UnknownEvidence,
            new ReviewResultValidator().Validate(normal, true, unknownEvidence).ReasonCode);
    }

    [TestMethod]
    public async Task VersionOrPromptMismatch_IsInconclusive()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var snapshot = ReviewFixtures.Snapshot(job);
        var staleObjective = ReviewFixtures.Result(snapshot, objectiveVersion: "objective-v2");
        Assert.AreEqual(ReviewErrorCodes.VersionMismatch,
            new ReviewResultValidator().Validate(snapshot, true, staleObjective).ReasonCode);
        var stalePrompt = ReviewFixtures.Result(snapshot, promptVersion: "prompt-v0");
        Assert.AreEqual(ReviewErrorCodes.VersionMismatch,
            new ReviewResultValidator().Validate(snapshot, true, stalePrompt).ReasonCode);
    }

    [TestMethod]
    public async Task CallerSuppliedEvidence_CanNeverBecomeCheckVerified()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var caller = new ReviewEvidenceItem("evidence-1", EvidenceKind.CallerSupplied, "caller", "caller://text",
            "hash", DateTimeOffset.UtcNow, "caller", Truncated: false, CheckExitCode: "0");
        var snapshot = ReviewFixtures.Snapshot(job, evidence: [caller]);
        var result = ReviewFixtures.Result(snapshot,
            criteria:
            [
                new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"]),
                new("criterion-2", CriterionOutcomeValue.Supported, ["evidence-1"]),
            ]);
        var outcome = new ReviewResultValidator().Validate(snapshot, true, result);
        Assert.AreEqual(ReviewFindingValue.Satisfied, outcome.Finding);
        Assert.AreEqual(ReviewEvidenceLevel.ModelAssessed, outcome.Level,
            "Caller-supplied strings and arbitrary paths are not verified evidence.");
    }

    [TestMethod]
    public async Task ControlledCheckReports_EarnCheckVerified()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var checks = ReviewFixtures.DefaultEvidence();
        checks[0] = checks[0] with { Kind = EvidenceKind.CheckReport, CheckExitCode = "0" };
        var snapshot = ReviewFixtures.Snapshot(job, evidence: [.. checks]);
        var outcome = new ReviewResultValidator().Validate(snapshot, true, ReviewFixtures.Result(snapshot));
        Assert.AreEqual(ReviewEvidenceLevel.CheckVerified, outcome.Level);
    }

    [TestMethod]
    public async Task ModelSatisfied_ButCriterionNotSupported_BecomesNeedsWork()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var snapshot = ReviewFixtures.Snapshot(job);
        var result = ReviewFixtures.Result(snapshot, ReviewFindingValue.Satisfied,
            [
                new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"]),
                new("criterion-2", CriterionOutcomeValue.NotSupported, ["evidence-2"]),
            ]);
        var outcome = new ReviewResultValidator().Validate(snapshot, true, result);
        Assert.AreEqual(ReviewFindingValue.NeedsWork, outcome.Finding, "A model Accept cannot override a failed criterion.");
    }

    [TestMethod]
    public async Task UncoveredRequiredCriterion_IsInconclusive()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var snapshot = ReviewFixtures.Snapshot(job);
        var partial = ReviewFixtures.Result(snapshot,
            criteria: [new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"])]);
        var outcome = new ReviewResultValidator().Validate(snapshot, true, partial);
        Assert.AreEqual(ReviewFindingValue.Inconclusive, outcome.Finding);
        Assert.AreEqual(ReviewErrorCodes.UnknownCriterion, outcome.ReasonCode);
    }
}
