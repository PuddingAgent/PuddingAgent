using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 回归：Task.Status == Completed 只算完成提议；完成必须全部必需条件都有
/// 同版本、同定义 hash、同输入指纹的受控检查结果（S1-b：不再区分验证作用域、不数剩余 WorkUnit）。
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
        IReadOnlyList<GoalCriterion>? criteria = null,
        IReadOnlyList<GoalCheckSpec>? checks = null,
        IReadOnlyList<GoalCheckReport>? reports = null,
        string? assistantOutputTurnId = null) => new()
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
        Criteria = criteria ?? [],
        Checks = checks ?? [],
        CheckReports = reports ?? [],
        AssistantOutputTurnId = assistantOutputTurnId,
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
            criteria: [Criterion("build", revision: 2), Criterion("test", revision: 2)],
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreNotEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual("acceptance_not_verified", decision.BlockerCode);
    }

    [TestMethod]
    public async Task VerifiedCriteria_WithoutTaskCompletion_Completes()
    {
        // S1-b：全必需条件同版本 passed ⇒ Complete；Task 未终态不再是「只推进」的理由
        // （G92-1 P2 否决族 Blocked/Failed/Cancelled/NeedsReview 分支仍未变）。
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "InProgress",
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task IncompleteEvidence_BlocksBeforeAnyCompletionClaim()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
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

    // ---------------- G92-1 S1-c（片 5 接线）端到端回归 ----------------
    // 片 5 缺口：verifier 调用 GoalCheckEvidencePolicy.Evaluate 时未传 assistantOutputTurnId
    // ⇒ text-assertion 永远 fail-closed。修复链：GoalSettlementCandidate.FinalAssistantReply
    // → ToCapsule().AssistantOutputTurnId → verifier 调用点 → policy 的 assistant-output 证据
    // 绑定校验。以下测试锁全链接线。policy 单元行为（前缀/turnId 匹配矩阵）由
    // GoalCheckEvidencePolicyTextAssertionTests 覆盖，runner 的 D1 文本精确比较由
    // GoalCheckTextAssertionRunnerTests 覆盖——此处不重复，只锁「接线不断」。

    [TestMethod]
    public async Task TextAssertion_WithBoundAssistantOutputTurnId_CompletesThroughVerifier()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            assistantOutputTurnId: "turn-1",
            criteria: [Criterion("reply-ok")],
            checks: [Spec("reply-ok", GoalVerificationSpecKinds.TextAssertion, expectedTests: null)],
            reports:
            [
                Report(
                    "reply-ok",
                    GoalVerificationSpecKinds.TextAssertion,
                    evidence: ["assistant-output:turn-1@7"],
                    executed: null, passed: null, failed: null),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task TextAssertion_WithoutAssistantOutputTurnId_FailsClosedWithEvidenceMissing()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        // capsule.AssistantOutputTurnId == null（终态 reply 不可得）⇒ fail-closed：
        // 终态 failed(evidence_missing)，不回 pending、不静默放行、不是 wait。
        var decision = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            assistantOutputTurnId: null,
            criteria: [Criterion("reply-ok")],
            checks: [Spec("reply-ok", GoalVerificationSpecKinds.TextAssertion, expectedTests: null)],
            reports:
            [
                Report(
                    "reply-ok",
                    GoalVerificationSpecKinds.TextAssertion,
                    evidence: ["assistant-output:turn-1@7"],
                    executed: null, passed: null, failed: null),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(1, decision.CriterionResults.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, decision.CriterionResults[0].Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, decision.CriterionResults[0].FailureCode);
        Assert.AreEqual(1, decision.UnmetCriteria.Count);
        StringAssert.StartsWith(decision.UnmetCriteria[0], "reply-ok: evidence_missing");
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task TextAssertion_TurnIdOrdinalExact_RejectsContainsLikeMatches()
    {
        var verifier = new ConservativeGoalIterationVerifier();

        // 反例 1（contains）：期望 turnId "OK" 不得被 "NOT OK" 以包含语义匹配。
        var contains = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            assistantOutputTurnId: "OK",
            criteria: [Criterion("reply-ok")],
            checks: [Spec("reply-ok", GoalVerificationSpecKinds.TextAssertion, expectedTests: null)],
            reports:
            [
                Report(
                    "reply-ok",
                    GoalVerificationSpecKinds.TextAssertion,
                    evidence: ["assistant-output:NOT OK@7"],
                    executed: null, passed: null, failed: null),
            ]));

        // 反例 2（超串前缀混淆）：turn-1 不得被 turn-10 匹配。
        var superstring = await verifier.VerifyAsync(Capsule(
            taskStatus: "Completed",
            assistantOutputTurnId: "turn-1",
            criteria: [Criterion("reply-ok")],
            checks: [Spec("reply-ok", GoalVerificationSpecKinds.TextAssertion, expectedTests: null)],
            reports:
            [
                Report(
                    "reply-ok",
                    GoalVerificationSpecKinds.TextAssertion,
                    evidence: ["assistant-output:turn-10@7"],
                    executed: null, passed: null, failed: null),
            ]));

        foreach (var decision in new[] { contains, superstring })
        {
            Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
            Assert.AreEqual("criterion_failed", decision.BlockerCode);
            Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, decision.CriterionResults[0].FailureCode);
            Assert.AreEqual(
                GoalSettlementDispositions.Repair,
                GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        }
    }

    [TestMethod]
    public async Task SettlementCandidate_ToCapsule_FillsAssistantOutputTurnId_AndCompletesThroughVerifier()
    {
        var candidate = TextAssertionCandidate(Reply("turn-1", sequence: 7, text: "OK"));

        var capsule = candidate.ToCapsule();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(capsule);

        // 接线锁：候选的 FinalAssistantReply.TurnId 必须原样进入胶囊（ToCapsule 填充）。
        Assert.AreEqual("turn-1", capsule.AssistantOutputTurnId);
        // 全链：胶囊 → verifier → policy，合规 assistant-output 证据 ⇒ Complete。
        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.AreEqual(
            GoalSettlementDispositions.Complete,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    [TestMethod]
    public async Task SettlementCandidate_WithoutFinalReply_FailsClosedAtVerifier()
    {
        // FinalAssistantReply 不可得（turn.completed 事件缺失 / payload 不可解析）⇒
        // ToCapsule 产出 AssistantOutputTurnId=null ⇒ verifier 全链 fail-closed，绝不 Complete。
        var candidate = TextAssertionCandidate(finalReply: null);

        var capsule = candidate.ToCapsule();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(capsule);

        Assert.IsNull(capsule.AssistantOutputTurnId);
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
        Assert.AreEqual(1, decision.CriterionResults.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, decision.CriterionResults[0].Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, decision.CriterionResults[0].FailureCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    /// <summary>片 3：canonical Turn 终态最终 assistant 输出（turn.completed 的 payload.reply 形状）。</summary>
    private static GoalFinalAssistantReply Reply(string turnId, long sequence, string text) => new()
    {
        TurnId = turnId,
        Sequence = sequence,
        Text = text,
    };

    /// <summary>text-assertion 单条件候选：合同/检查/报告身份一致，仅 FinalAssistantReply 可变。</summary>
    private static GoalSettlementCandidate TextAssertionCandidate(GoalFinalAssistantReply? finalReply) => new()
    {
        GoalIterationId = "iter-1",
        GoalRunId = "goal-1",
        WorkspaceId = "ws-1",
        ConversationId = "conv-1",
        AgentInstanceId = "agent-1",
        ActivationEpoch = 1,
        AggregateVersion = 0,
        IterationNo = 1,
        MaxIterations = 10,
        IterationsStarted = 1,
        Objective = "修复全部失败测试并证明普通开发任务可继续执行",
        ObjectiveVersion = 1,
        TurnId = "turn-1",
        TerminalKind = "completed",
        TerminalSequence = 7,
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        TaskId = "task-1",
        TaskStatus = "Completed",
        Criteria = [Criterion("reply-ok")],
        Checks = [Spec("reply-ok", GoalVerificationSpecKinds.TextAssertion, expectedTests: null)],
        CheckReports =
        [
            Report(
                "reply-ok",
                GoalVerificationSpecKinds.TextAssertion,
                evidence: ["assistant-output:turn-1@7"],
                executed: null, passed: null, failed: null),
        ],
        FinalAssistantReply = finalReply,
        EvidenceComplete = true,
    };
}
