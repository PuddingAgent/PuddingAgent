using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 回归：Task.Status == Completed 只算完成提议；完成必须全部必需条件都有
/// 同版本、同定义 hash、同输入指纹的受控检查结果，且显式声明 goal 作用域（无绑定 Task，或已知无剩余 WorkUnit）。
/// </summary>
[TestClass]
public sealed class ConservativeGoalIterationVerifierTests
{
    private const string DefinitionHash = "def-1";
    private const string Fingerprint = "fp-1";

    private static GoalCriterion Criterion(string id, int revision = 1, bool required = true) => new()
    {
        Id = id,
        Revision = revision,
        Requirement = $"criterion {id}",
        Required = required,
        Kind = GoalVerificationSpecKinds.Test,
    };

    private static GoalCheckSpec Spec(
        string criterionId,
        string kind = GoalVerificationSpecKinds.Test,
        int? expectedTests = 10,
        int revision = 1) => new()
    {
        CheckId = $"check-{criterionId}",
        CriterionId = criterionId,
        CriterionRevision = revision,
        Kind = kind,
        DefinitionRef = "checks/regression.md",
        DefinitionHash = DefinitionHash,
        InputRefs = ["Source/PuddingCore"],
        InputFingerprint = Fingerprint,
        ExpectedEvidence = "fresh run report with all required cases",
        ExpectedTestCount = expectedTests,
    };

    private static GoalCheckReport Report(
        string criterionId,
        string kind = GoalVerificationSpecKinds.Test,
        string status = GoalCriterionResultStatuses.Passed,
        int revision = 1,
        string fingerprint = Fingerprint,
        string definitionHash = DefinitionHash,
        string? runnerId = "goal-check-runner",
        string? invocationId = "inv-1",
        string? reportRef = "reports/run.txt",
        int? exitCode = 0,
        int? executed = 12,
        int? passed = 12,
        int? failed = 0,
        bool? unfinishedBackgroundProcess = false,
        IReadOnlyList<string>? evidence = null) => new()
    {
        CheckId = $"check-{criterionId}",
        CriterionId = criterionId,
        CriterionRevision = revision,
        Status = status,
        DefinitionHash = definitionHash,
        InputFingerprint = fingerprint,
        RunnerId = runnerId,
        InvocationId = invocationId,
        ReportRef = reportRef,
        EvidenceRefs = evidence ?? [$"report:reports/run-{criterionId}.txt"],
        ExitCode = exitCode,
        ExecutedTestCount = executed,
        PassedTestCount = passed,
        FailedTestCount = failed,
        HasUnfinishedBackgroundProcess = unfinishedBackgroundProcess,
    };

    private static GoalEvidenceCapsule Capsule(
        string terminalKind = "completed",
        string? taskStatus = "InProgress",
        string? taskId = "task-1",
        bool evidenceComplete = true,
        bool pendingFacts = false,
        int? remainingWorkUnits = null,
        IReadOnlyList<GoalCriterion>? criteria = null,
        IReadOnlyList<GoalCheckSpec>? checks = null,
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
        TaskId = taskId,
        TaskStatus = taskStatus,
        TaskAcceptanceCriteria = "全部失败测试通过",
        HasPendingExecutionFacts = pendingFacts,
        EvidenceComplete = evidenceComplete,
        RemainingWorkUnits = remainingWorkUnits,
        Criteria = criteria ?? [],
        Checks = checks ?? [],
        CheckReports = reports ?? [],
    };

    private static readonly GoalCriterion[] RequiredCriteria = [Criterion("build"), Criterion("test")];

    private static readonly GoalCheckSpec[] RequiredChecks =
    [
        Spec("build", GoalVerificationSpecKinds.Build, expectedTests: null),
        Spec("test", GoalVerificationSpecKinds.Test),
    ];

    private static readonly GoalCheckReport[] CleanReports =
    [
        Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
        Report("test"),
    ];

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
    public async Task Criteria_WithoutVersionedChecks_DoNotComplete()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            criteria: RequiredCriteria));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("check_contract_missing", decision.BlockerCode);
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
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(2, decision.CriterionResults.Count);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task UnknownRemainingWorkUnits_OnlyAdvances()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task StandaloneGoal_WithAllCriteriaPassed_Completes()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: null,
            taskId: null,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task BuildPassed_TestFailed_DoesNotAdvance()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
                Report("test", executed: 12, passed: 11, failed: 1),
            ]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task TaskCompleted_WithUnrunChecks_IsOnlyAProposal()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: [Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null)]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("check_results_pending", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task PendingChecks_WaitInsteadOfRepairing()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, status: GoalCriterionResultStatuses.Pending, executed: null, passed: null, failed: null),
                Report("test", status: GoalCriterionResultStatuses.Pending),
            ]));

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
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
                Report("test", status: GoalCriterionResultStatuses.Failed, passed: 11, failed: 1),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task ZeroTests_CannotPass()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
                Report("test", executed: 0, passed: 0, failed: 0),
            ]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task UnfinishedBackgroundProcess_Waits()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
                Report("test", unfinishedBackgroundProcess: true),
            ]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("check_results_pending", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task StaleGreenReport_IsNotCompletion()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, fingerprint: "fp-2", executed: null, passed: null, failed: null),
                Report("test"),
            ]));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task UnverifiedRevision_DoesNotCount()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: [Criterion("build", revision: 2), Criterion("test", revision: 2)],
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
    }

    [TestMethod]
    public async Task VerifiedCriteria_WithoutTaskCompletion_Advances()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task IncompleteEvidence_BlocksBeforeAnyCompletionClaim()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            evidenceComplete: false,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("evidence_incomplete", decision.BlockerCode);
        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
    }
}
