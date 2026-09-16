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
        IReadOnlyList<string>? evidence = null,
        string? failureCode = null,
        string? message = null) => new()
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
        FailureCode = failureCode,
        Message = message,
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
    public async Task AcceptanceContractMissing_IsARepairStep_NotAnEternalWait()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(taskStatus: "Completed"));

        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.AreNotEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsFalse(string.IsNullOrWhiteSpace(decision.NextAction));
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

    // ── T5：UnmetCriteria 必须从真实证据填充（此前是恒 [] 的死字段，裁决说不出哪条条件没满足）──

    [TestMethod]
    public async Task FailedCheck_FillsUnmetCriteria_TraceableToReport()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report(
                    "build", GoalVerificationSpecKinds.Build,
                    status: GoalCriterionResultStatuses.Failed,
                    exitCode: 1,
                    executed: null, passed: null, failed: null,
                    failureCode: "non_zero_exit_code",
                    message: "dotnet build exited with code 1."),
                Report("test"),
            ]));

        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(1, decision.UnmetCriteria.Count);
        StringAssert.Contains(decision.UnmetCriteria[0], "build: non_zero_exit_code");
        StringAssert.Contains(decision.UnmetCriteria[0], "dotnet build exited with code 1.");
    }

    [TestMethod]
    public async Task WaitingCheck_FillsUnmetCriteria_WaitFamily()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build, executed: null, passed: null, failed: null),
                Report(
                    "test",
                    status: GoalCriterionResultStatuses.Waiting,
                    failureCode: "check_timeout",
                    message: "The check did not finish before its deadline."),
            ]));

        // 检查超时/等待也是「未通过」：清单必须来自真实报告，且处置保持等待族而非失败族。
        Assert.AreEqual("check_results_pending", decision.BlockerCode);
        Assert.AreEqual(1, decision.UnmetCriteria.Count);
        StringAssert.Contains(decision.UnmetCriteria[0], "test: check_timeout");
        StringAssert.Contains(decision.UnmetCriteria[0], "The check did not finish before its deadline.");
        Assert.AreEqual(
            GoalSettlementDispositions.Wait,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task AllChecksPassed_UnmetCriteriaStaysEmptyList()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            remainingWorkUnits: 0,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.IsNotNull(decision.UnmetCriteria);
        Assert.IsTrue(decision.UnmetCriteria.Count == 0, "unmet criteria must stay an empty list, never null or placeholder");
    }

    [TestMethod]
    public async Task MultipleFailedChecks_EveryFailureListedInStableCheckOrder()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        // 报告故意按 test→build 顺序给出：清单顺序必须跟随声明的检查定义（build→test），稳定可复现。
        var decision = await verifier.VerifyAsync(Capsule(
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report(
                    "test",
                    status: GoalCriterionResultStatuses.Failed,
                    exitCode: 1,
                    failureCode: "tests_failed",
                    message: "2 of 12 test cases failed."),
                Report(
                    "build", GoalVerificationSpecKinds.Build,
                    status: GoalCriterionResultStatuses.Failed,
                    exitCode: 1,
                    executed: null, passed: null, failed: null,
                    failureCode: "non_zero_exit_code",
                    message: "build exited with code 1."),
            ]));

        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(2, decision.UnmetCriteria.Count);
        StringAssert.StartsWith(decision.UnmetCriteria[0], "build: non_zero_exit_code");
        StringAssert.StartsWith(decision.UnmetCriteria[1], "test: tests_failed");
    }

    [TestMethod]
    public async Task RequiredCriterionWithoutDeclaredCheck_AppearsInUnmetCriteria()
    {
        var verifier = new ConservativeGoalIterationVerifier();
        var criteria = new[] { Criterion("build"), Criterion("test"), Criterion("artifact-present") };
        var checks = new[] { RequiredChecks[0], RequiredChecks[1] };

        var decision = await verifier.VerifyAsync(Capsule(
            criteria: criteria,
            checks: checks,
            reports: CleanReports));

        // build/test 通过，但第三个必需条件没有任何版本化检查：不得 vacuous pass，且要能说出差集。
        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
        Assert.AreEqual(1, decision.UnmetCriteria.Count);
        StringAssert.Contains(decision.UnmetCriteria[0], "artifact-present: check_not_declared");
    }
}
