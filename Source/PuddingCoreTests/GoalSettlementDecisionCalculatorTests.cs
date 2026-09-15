using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;

namespace PuddingCoreTests;

/// <summary>
/// G92-0 回归：Turn 结束、Task Completed、EvidenceComplete 都不是验收证据。
/// 只有当前单元的必需条件通过真实检查，才允许推进或完成。
/// </summary>
[TestClass]
public sealed class GoalSettlementDecisionCalculatorTests
{
    private static GoalCriterion Criterion(string id, int revision = 1, bool required = true) => new()
    {
        Id = id,
        Revision = revision,
        Requirement = $"criterion {id}",
        Required = required,
    };

    private static GoalCriterionResult Result(string id, string status, int revision = 1) => new()
    {
        CriterionId = id,
        CriterionRevision = revision,
        Status = status,
    };

    private static GoalVerificationDecision Decision(
        GoalVerificationVerdict verdict,
        IReadOnlyList<GoalCriterion>? criteria = null,
        IReadOnlyList<GoalCriterionResult>? results = null,
        string? blockerCode = null,
        string reason = "test")
        => new()
        {
            Verdict = verdict,
            Reason = reason,
            EvidenceRefs = ["turn:1:terminal:1"],
            Criteria = criteria ?? [],
            CriterionResults = results ?? [],
            BlockerCode = blockerCode,
        };

    [TestMethod]
    public void CompletedTurn_WithPlanOnly_DoesNotVerifyWorkUnit()
    {
        // 复现：一轮完成只写了计划/部分代码，没有任何条件结果。
        var decision = Decision(
            GoalVerificationVerdict.Complete,
            criteria: [Criterion("build"), Criterion("test")],
            results: []);

        Assert.IsFalse(GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision));
        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void EmptyContract_IsNeverAVacuousPass()
    {
        var decision = Decision(GoalVerificationVerdict.Complete);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.AreNotEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void PartiallyVerifiedCriteria_StayOnCurrentUnit()
    {
        var decision = Decision(
            GoalVerificationVerdict.Complete,
            criteria: [Criterion("build"), Criterion("test")],
            results: [Result("build", GoalCriterionResultStatuses.Passed)]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void FailedCriterion_RepairsCurrentUnit()
    {
        var decision = Decision(
            GoalVerificationVerdict.Continue,
            criteria: [Criterion("build"), Criterion("test")],
            results:
            [
                Result("build", GoalCriterionResultStatuses.Passed),
                Result("test", GoalCriterionResultStatuses.Failed),
            ]);

        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void StaleRevisionResult_DoesNotCount()
    {
        var decision = Decision(
            GoalVerificationVerdict.Complete,
            criteria: [Criterion("build", revision: 2)],
            results: [Result("build", GoalCriterionResultStatuses.Passed, revision: 1)]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision));
    }

    [TestMethod]
    public void OptionalCriterion_DoesNotBlockAdvance()
    {
        var decision = Decision(
            GoalVerificationVerdict.Continue,
            criteria: [Criterion("build"), Criterion("docs", required: false)],
            results: [Result("build", GoalCriterionResultStatuses.Passed)]);

        Assert.IsTrue(GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void VerifiedCriteria_WithCompleteVerdict_CompletesGoal()
    {
        var decision = Decision(
            GoalVerificationVerdict.Complete,
            criteria: [Criterion("build"), Criterion("test")],
            results:
            [
                Result("build", GoalCriterionResultStatuses.Passed),
                Result("test", GoalCriterionResultStatuses.Passed),
            ]);

        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void DependencyWait_IsWaitingNotRepairOrStop()
    {
        var decision = Decision(
            GoalVerificationVerdict.Blocked,
            blockerCode: "approval_review_profile_not_configured");

        Assert.AreEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(GoalSettlementDecisionCalculator.IsWaiting(decision));
    }

    [TestMethod]
    public void UnrecoverableBlocker_StopsButRecoverableDoesNot()
    {
        Assert.AreEqual(
            GoalSettlementDispositions.Stop,
            GoalSettlementDecisionCalculator.ComputeDisposition(
                Decision(GoalVerificationVerdict.Blocked, blockerCode: "task_plan_state_invalid")));

        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(
                Decision(GoalVerificationVerdict.Blocked, blockerCode: "build_failed")));
    }

    [TestMethod]
    public void NeedsUserAndUnsafe_AreDistinctOutcomes()
    {
        Assert.AreEqual(
            GoalSettlementDispositions.NeedsUser,
            GoalSettlementDecisionCalculator.ComputeDisposition(
                Decision(GoalVerificationVerdict.NeedsUser)));

        Assert.AreEqual(
            GoalSettlementDispositions.Stop,
            GoalSettlementDecisionCalculator.ComputeDisposition(
                Decision(GoalVerificationVerdict.Unsafe)));
    }
}
