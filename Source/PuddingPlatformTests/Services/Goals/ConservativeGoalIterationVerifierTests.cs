using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 回归：Task.Status == Completed 只算完成提议，真正的完成要求全部必需条件
/// 都有同版本且 passed 的受控检查结果；空合同、待检查、检查失败分别映射到不同处置。
/// </summary>
[TestClass]
public sealed class ConservativeGoalIterationVerifierTests
{
    private static GoalCriterion Criterion(string id, int revision = 1, bool required = true) => new()
    {
        Id = id,
        Revision = revision,
        Requirement = $"criterion {id}",
        Required = required,
        Kind = GoalVerificationSpecKinds.Test,
    };

    private static GoalCheckReport Report(string criterionId, string status, int revision = 1) => new()
    {
        CheckId = $"check-{criterionId}",
        CriterionId = criterionId,
        CriterionRevision = revision,
        Status = status,
        EvidenceRefs = [$"artifact:check-{criterionId}"],
    };

    private static GoalEvidenceCapsule Capsule(
        string terminalKind = "completed",
        string? taskStatus = "InProgress",
        bool evidenceComplete = true,
        bool pendingFacts = false,
        IReadOnlyList<GoalCriterion>? criteria = null,
        IReadOnlyList<GoalCheckReport>? reports = null) => new()
    {
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        AggregateVersion = 0,
        IterationNo = 1,
        Objective = "修复全部失败测试并证明普通开发任务可继续执行",
        ObjectiveVersion = 1,
        RemainingIterations = 10,
        TurnId = "turn-1",
        TerminalKind = terminalKind,
        TerminalSequence = 7,
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        TaskId = "task-1",
        TaskStatus = taskStatus,
        TaskAcceptanceCriteria = "全部失败测试通过",
        HasPendingExecutionFacts = pendingFacts,
        EvidenceComplete = evidenceComplete,
        Criteria = criteria ?? [],
        CheckReports = reports ?? [],
    };

    private static readonly GoalCriterion[] RequiredCriteria =
        [Criterion("build"), Criterion("test")];

    [TestMethod]
    public async Task TaskCompleted_WithoutContract_DoesNotComplete()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(taskStatus: "Completed"));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task TaskCompleted_WithAllCriteriaPassed_Completes()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            criteria: RequiredCriteria,
            reports: [Report("build", GoalCriterionResultStatuses.Passed), Report("test", GoalCriterionResultStatuses.Passed)]));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(2, decision.CriterionResults.Count);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task TaskCompleted_WithPartialCriteria_IsOnlyAProposal()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            criteria: RequiredCriteria,
            reports: [Report("build", GoalCriterionResultStatuses.Passed)]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
        StringAssert.Contains(decision.Reason, "proposed");
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task PendingChecks_WaitInsteadOfRepairing()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            reports: [Report("build", GoalCriterionResultStatuses.Pending)]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("check_results_pending", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task FailedCheck_RepairsCurrentUnit()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            reports:
            [
                Report("build", GoalCriterionResultStatuses.Passed),
                Report("test", GoalCriterionResultStatuses.Failed),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task VerifiedCriteria_WithoutTaskCompletion_Advances()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            reports: [Report("build", GoalCriterionResultStatuses.Passed), Report("test", GoalCriterionResultStatuses.Passed)]));

        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task UnverifiedRevision_DoesNotCount()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            criteria: [Criterion("build", revision: 2), Criterion("test", revision: 2)],
            reports:
            [
                Report("build", GoalCriterionResultStatuses.Passed, revision: 1),
                Report("test", GoalCriterionResultStatuses.Passed, revision: 1),
            ]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
    }

    [TestMethod]
    public async Task IncompleteEvidence_BlocksBeforeAnyCompletionClaim()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            evidenceComplete: false,
            criteria: RequiredCriteria,
            reports: [Report("build", GoalCriterionResultStatuses.Passed), Report("test", GoalCriterionResultStatuses.Passed)]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("evidence_incomplete", decision.BlockerCode);
        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
    }
}
