using PuddingGoalPolicy.Contracts;
using PuddingPlatform.Services.Reviews;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-04 S5 review-fact mapping: model-advice-only Satisfied can never become Satisfied, version/artifact mismatch
/// is Stale, missing review stays Missing when required, and an optional review is NotRequired.
/// </summary>
[TestClass]
public sealed class GoalReviewFactMapperTests
{
    private const string Artifact = "artifact-hash";
    private const string Acceptance = "acceptance-v1";

    [TestMethod]
    public void SatisfiedRequiresCheckVerifiedEvidence()
    {
        var verified = GoalReviewFactMapper.Map(Projection("Satisfied", "CheckVerified"), Artifact, Acceptance, true);
        Assert.AreEqual(ReviewFact.Satisfied, verified.Review);
        Assert.IsFalse(verified.Stale);

        var adviceOnly = GoalReviewFactMapper.Map(Projection("Satisfied", "Observed"), Artifact, Acceptance, true);
        Assert.AreEqual(ReviewFact.NeedsUser, adviceOnly.Review);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewNeedsUser, adviceOnly.ReasonCode);
    }

    [TestMethod]
    public void FindingsMapToTheirPolicyFacts()
    {
        Assert.AreEqual(ReviewFact.NeedsWork,
            GoalReviewFactMapper.Map(Projection("NeedsWork", "Observed"), Artifact, Acceptance, true).Review);
        Assert.AreEqual(ReviewFact.NeedsUser,
            GoalReviewFactMapper.Map(Projection("NeedsUser", "ModelAssessed"), Artifact, Acceptance, true).Review);
        Assert.AreEqual(ReviewFact.Unknown,
            GoalReviewFactMapper.Map(Projection("Inconclusive", "Observed"), Artifact, Acceptance, true).Review);
        Assert.AreEqual(ReviewFact.Unknown,
            GoalReviewFactMapper.Map(Projection("SomethingElse", "Observed"), Artifact, Acceptance, true).Review);
    }

    [TestMethod]
    public void ArtifactOrContractMismatchIsStale()
    {
        var artifact = GoalReviewFactMapper.Map(Projection("Satisfied", "CheckVerified", artifactSetHash: "old"),
            Artifact, Acceptance, true);
        Assert.AreEqual(ReviewFact.Stale, artifact.Review);
        Assert.IsTrue(artifact.Stale);

        var contract = GoalReviewFactMapper.Map(Projection("Satisfied", "CheckVerified", acceptance: "acceptance-v0"),
            Artifact, Acceptance, true);
        Assert.AreEqual(ReviewFact.Stale, contract.Review);
        Assert.IsTrue(contract.Stale);
    }

    [TestMethod]
    public void MissingProjectionIsMissingWhenRequired()
    {
        var required = GoalReviewFactMapper.Map(null, Artifact, Acceptance, reviewRequired: true);
        Assert.AreEqual(ReviewFact.Missing, required.Review);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewMissing, required.ReasonCode);

        var optional = GoalReviewFactMapper.Map(null, Artifact, Acceptance, reviewRequired: false);
        Assert.AreEqual(ReviewFact.NotRequired, optional.Review);
        Assert.IsNull(optional.ReasonCode);
    }

    [TestMethod]
    public void ReviewFactFeedsThePurePolicyWithoutCompletingOnAdvice()
    {
        var policy = new PuddingGoalPolicy.GoalContinuationPolicy();
        var facts = Facts(GoalReviewFactMapper.Map(Projection("Satisfied", "Observed"), Artifact, Acceptance, true));
        var decision = policy.Decide(Snapshot(), facts);
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, decision.Kind,
            "Model advice must never complete a Goal without verified evidence.");
    }

    private static GoalPolicyFacts Facts(GoalReviewFact review) => new("goal-1", 1, 2, GoalPhaseFact.Active,
        Cancelled: false, Paused: false, AuthorizationRevoked: false, ObjectiveVersionChanged: false,
        ReviewStale: review.Stale, Review: review.Review, BudgetExhausted: false, MaxRoundsReached: false,
        HasUnmetRequiredCriteria: false, EvidenceSufficient: true, NoUnresolvedExecutionFacts: true,
        RepairAllowed: true, RepairUsed: 0, HasExecutableNextAction: true, ConsecutiveNoProgress: 0,
        NextActionId: "next-1", BlockerReason: null);

    private static ContinuationPolicySnapshot Snapshot() => new(InteractionMode.Goal, "policy-v1", ReviewRequired: true,
        RepairLimit: 2, NoProgressLimit: 3, RootBudgetId: "budget-1", AllowedCapabilities: ["read"],
        ResumePolicy: "auto");

    private static ReviewOutcomeProjection Projection(string finding, string level,
        string artifactSetHash = Artifact, string? acceptance = Acceptance) => new("review-1", "job-1", finding, level,
        3, artifactSetHash, acceptance, 42);
}
