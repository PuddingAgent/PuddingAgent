using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;

namespace PuddingCoreTests;

/// <summary>
/// G92-1 回归（验收标准 1/2）：同一必需条件的多个检查必须共同裁决——
/// "build passed + test failed/waiting" 绝不能 Advance/Complete，且与结果列表顺序无关。
/// </summary>
[TestClass]
public sealed class GoalSettlementCriterionAggregationTests
{
    private static GoalCriterion Criterion(string id, int revision = 1, bool required = true) => new()
    {
        Id = id,
        Revision = revision,
        Requirement = $"criterion {id}",
        Required = required,
    };

    private static GoalCriterionResult Result(string criterionId, string checkName, string status, int revision = 1) => new()
    {
        CriterionId = criterionId,
        CriterionRevision = revision,
        Status = status,
        EvidenceRefs = [$"check:{checkName}"],
        InputFingerprint = "fp-1",
    };

    private static GoalVerificationDecision Decision(
        IReadOnlyList<GoalCriterion> criteria,
        IReadOnlyList<GoalCriterionResult> results,
        GoalVerificationVerdict verdict = GoalVerificationVerdict.Continue) => new()
    {
        Verdict = verdict,
        Reason = "aggregation probe",
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        Criteria = criteria,
        CriterionResults = results,
    };

    private static readonly GoalCriterion[] TwoRequiredCriteria = [Criterion("build"), Criterion("test")];

    [TestMethod]
    public void TwoChecksForOneCriterion_BuildPassedTestFailed_DoesNotAdvance()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
                Result("test", "regression-tests", GoalCriterionResultStatuses.Failed),
            ]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.IsTrue(GoalSettlementDecisionCalculator.HasFailedCriteria(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void TwoChecksForOneCriterion_SameVerdict_WhenOrderIsReversed()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("test", "regression-tests", GoalCriterionResultStatuses.Failed),
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
            ]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.IsTrue(GoalSettlementDecisionCalculator.HasFailedCriteria(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void TwoChecksForOneCriterion_BuildPassedTestWaiting_NeverAdvances()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
                Result("test", "regression-tests", GoalCriterionResultStatuses.Waiting),
            ],
            GoalVerificationVerdict.Complete);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.AreNotEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.AreNotEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void InvalidatedCheck_OutranksPassedSibling()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
                Result("test", "regression-tests", GoalCriterionResultStatuses.Invalidated),
            ]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.IsFalse(GoalSettlementDecisionCalculator.HasFailedCriteria(decision));
        Assert.AreNotEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void AllChecksPassed_ForAllRequiredCriteria_Advances()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("build", "postcondition", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
                Result("test", "regression-tests", GoalCriterionResultStatuses.Passed),
            ]);

        Assert.IsTrue(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void MissingCriterionResult_DoesNotPass()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [Result("build", "build", GoalCriterionResultStatuses.Passed)]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
    }

    [TestMethod]
    public void EmptyCriteriaContract_DoesNotPass()
    {
        var decision = Decision([], [Result("build", "build", GoalCriterionResultStatuses.Passed)]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public void OptimisticCriterion_DoesNotMaskRequiredFailure()
    {
        var decision = Decision(
            [Criterion("build"), Criterion("test")],
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Failed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Passed),
            ]);

        Assert.IsFalse(GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision));
        Assert.IsTrue(GoalSettlementDecisionCalculator.HasFailedCriteria(decision));
    }

    [TestMethod]
    public void AggregateByCriterion_ProducesOneResultPerCriterionWithWorstStatus()
    {
        var decision = Decision(
            TwoRequiredCriteria,
            [
                Result("build", "build", GoalCriterionResultStatuses.Passed),
                Result("test", "unit-tests", GoalCriterionResultStatuses.Waiting),
                Result("test", "regression-tests", GoalCriterionResultStatuses.Failed),
            ]);

        var aggregated = GoalSettlementDecisionCalculator.AggregateByCriterion(decision);

        Assert.AreEqual(2, aggregated.Count);
        var test = aggregated.Single(item => item.CriterionId == "test");
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, test.Status);
        Assert.AreEqual("check:regression-tests", test.EvidenceRefs[0]);
    }
}
