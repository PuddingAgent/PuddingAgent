using PuddingCode.Goals;
using PuddingGoalPolicy.Contracts;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-04 opt-in Required Review: default policy keeps the old "no job means review not required" behavior; an explicit
/// policy makes a missing review block completion, while a satisfied same-version review still completes.
/// </summary>
[TestClass]
public sealed class GoalReviewCompletionGateTests
{
    [TestMethod]
    public async Task DefaultPolicy_NoJob_IsNotRequired()
    {
        var gate = new GoalReviewCompletionGate(new NoJobs());
        Assert.AreEqual(ReviewFact.NotRequired, (await gate.EvaluateAsync(Capsule())));
    }

    [TestMethod]
    public async Task RequiredPolicy_NoJob_IsMissing()
    {
        var gate = new GoalReviewCompletionGate(new NoJobs(), new Required(true));
        Assert.AreEqual(ReviewFact.Missing, (await gate.EvaluateAsync(Capsule())));
    }

    [TestMethod]
    public async Task RequiredPolicy_SatisfiedSameVersion_IsSatisfied()
    {
        var outcome = new ReviewOutcome(ReviewFindingValue.Satisfied, ReviewEvidenceLevel.CheckVerified, [], null);
        var job = new ReviewJobView("job-1", "review-1", Key("1"), true, "budget-1",
            DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Completed, 1, 1, 5, outcome, null,
            DateTimeOffset.UtcNow, "actor-1");
        var gate = new GoalReviewCompletionGate(new OneJob(job), new Required(true));
        Assert.AreEqual(ReviewFact.Satisfied, (await gate.EvaluateAsync(Capsule())));
    }

    private static GoalEvidenceCapsule Capsule() => new()
    {
        GoalRunId = "goal-1", ObjectiveVersion = 1, ActivationEpoch = 1, AggregateVersion = 1, IterationNo = 1,
        RemainingIterations = 10,
        Objective = "objective", TerminalKind = "completed", TerminalSequence = 1, TurnId = "turn-1",
        EvidenceRefs = ["turn:turn-1:terminal:1"], TaskId = "task-1", TaskStatus = "Completed",
    };

    private static ReviewLogicalKey Key(string objectiveVersion) => new("ws-1", "task-1", "goal-1", 1, 1,
        objectiveVersion, "acceptance-v1", "artifact-hash", "policy-v1");

    private sealed class Required(bool value) : IGoalReviewRequirementPolicy
    {
        public ValueTask<bool> IsRequiredAsync(GoalEvidenceCapsule capsule, CancellationToken ct = default)
            => ValueTask.FromResult(value);
    }

    private sealed class NoJobs : IReviewJobByGoalObjectiveReader
    {
        public Task<ReviewJobView?> GetLatestByGoalObjectiveAsync(string goalRunId, string objectiveVersion,
            CancellationToken ct = default) => Task.FromResult<ReviewJobView?>(null);
    }

    private sealed class OneJob(ReviewJobView job) : IReviewJobByGoalObjectiveReader
    {
        public Task<ReviewJobView?> GetLatestByGoalObjectiveAsync(string goalRunId, string objectiveVersion,
            CancellationToken ct = default) => Task.FromResult<ReviewJobView?>(job);
    }
}
