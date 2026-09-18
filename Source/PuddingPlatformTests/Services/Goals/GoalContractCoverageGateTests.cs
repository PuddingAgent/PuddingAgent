using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-a 合同覆盖门（判定表 A–F，规格 §2.1）：
/// A 纯门禁合同（source=bounded_planning）+ 全部 passed ⇒ Blocked/contract_coverage_insufficient（不可删负例）；
/// B 含 objective 证据合同（source=bounded_planning:objective_evidence）+ 全部 passed ⇒ Complete（不变）；
/// C/D 纯门禁合同 + failed/pending ⇒ 既有分支优先，不被覆盖门改写；
/// E 空合同/无检查定义 ⇒ 既有 acceptance_contract_missing / check_contract_missing 不变；
/// F Task-bound 且未达 goal scope ⇒ 既有行为不变（work_unit 全绿只推进）。
/// 另含 S1-a-2 胶囊透传断言（GoalSettlementCandidate.ToCapsule 携带合同来源）。
/// </summary>
[TestClass]
public sealed class GoalContractCoverageGateTests
{
    private const string DefinitionHash = "def-1";
    private const string Fingerprint = "fp-1";
    private const string PureEngineeringSource = GoalAcceptanceContractSources.BoundedPlanning;
    private const string WithObjectiveEvidenceSource =
        GoalAcceptanceContractSources.BoundedPlanningWithObjectiveEvidence;

    private static GoalCriterion Criterion(string id) => new()
    {
        Id = id,
        Revision = 1,
        Requirement = $"criterion {id}",
        Required = true,
        Kind = GoalVerificationSpecKinds.Test,
    };

    private static GoalCheckSpec Spec(
        string criterionId,
        string kind = GoalVerificationSpecKinds.Test,
        int? expectedTests = 10) => new()
    {
        CheckId = $"check-{criterionId}",
        CriterionId = criterionId,
        CriterionRevision = 1,
        Kind = kind,
        DefinitionRef = "checks/coverage-gate.md",
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
        string? failureCode = null,
        string? message = null)
    {
        var isBuildLike = string.Equals(kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal);
        var passed = string.Equals(status, GoalCriterionResultStatuses.Passed, StringComparison.Ordinal);

        // GoalCheckEvidencePolicy 第 7 步：test 类 passed 报告要求三个计数全部非 null 且自洽
        // （passed + failed == executed，executed > 0，failed == 0）；build 类只要求 exit 0 + 运行报告。
        return new GoalCheckReport
        {
            CheckId = $"check-{criterionId}",
            CriterionId = criterionId,
            CriterionRevision = 1,
            Status = status,
            DefinitionHash = DefinitionHash,
            InputFingerprint = Fingerprint,
            RunnerId = "goal-check-runner",
            InvocationId = "inv-1",
            ReportRef = $"reports/run-{criterionId}.txt",
            EvidenceRefs = [$"report:reports/run-{criterionId}.txt"],
            ExitCode = passed ? 0 : 1,
            ExecutedTestCount = isBuildLike ? null : 12,
            PassedTestCount = isBuildLike ? null : passed ? 12 : 11,
            FailedTestCount = isBuildLike ? null : passed ? 0 : 1,
            HasUnfinishedBackgroundProcess = false,
            FailureCode = failureCode,
            Message = message,
        };
    }

    private static GoalEvidenceCapsule Capsule(
        string? acceptanceContractSource,
        string taskStatus = "Completed",
        int? remainingWorkUnits = 0,
        IReadOnlyList<GoalCriterion>? criteria = null,
        IReadOnlyList<GoalCheckSpec>? checks = null,
        IReadOnlyList<GoalCheckReport>? reports = null) => new()
    {
        GoalRunId = "goal-gate-1",
        ActivationEpoch = 1,
        AggregateVersion = 0,
        IterationNo = 1,
        Objective = "交付业务功能并提供版本化证据",
        ObjectiveVersion = 1,
        RemainingIterations = 10,
        TurnId = "turn-gate-1",
        TerminalKind = "completed",
        TerminalSequence = 7,
        EvidenceRefs = ["turn:turn-gate-1:terminal:7"],
        TaskId = "task-gate-1",
        TaskStatus = taskStatus,
        TaskAcceptanceCriteria = "业务条件满足",
        HasPendingExecutionFacts = false,
        EvidenceComplete = true,
        RemainingWorkUnits = remainingWorkUnits,
        AcceptanceContractSource = acceptanceContractSource,
        Criteria = criteria ?? [],
        Checks = checks ?? [],
        CheckReports = reports ?? [],
    };

    private static readonly GoalCriterion[] RequiredCriteria = [Criterion("build"), Criterion("test")];

    private static readonly GoalCheckSpec[] RequiredChecks =
    [
        Spec("build", GoalVerificationSpecKinds.Build, expectedTests: null),
        Spec("test"),
    ];

    private static readonly GoalCheckReport[] CleanReports =
    [
        Report("build", GoalVerificationSpecKinds.Build),
        Report("test"),
    ];

    private static readonly ConservativeGoalIterationVerifier Verifier = new();

    // ── 判定表 A（不可删负例）：纯门禁合同 + 全绿 ⇒ 不得完成 ──

    [TestMethod]
    public async Task EngineeringGatesContract_AllPassed_BlockedWithCoverageInsufficient()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            PureEngineeringSource,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("contract_coverage_insufficient", decision.BlockerCode);
        // 处置必须是可执行的修复（有界合同整理），不是等待也不是终止。
        Assert.AreEqual(
            GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsNotNull(decision.NextAction);
        StringAssert.Contains(decision.NextAction, "contract refinement");
    }

    // ── 判定表 B：含 objective 证据的合同 + 全绿 ⇒ 完成路径保持不变 ──

    [TestMethod]
    public async Task ObjectiveEvidenceContract_AllPassed_StillCompletes()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            WithObjectiveEvidenceSource,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.IsNull(decision.BlockerCode);
    }

    // ── 判定表 C/D：失败/等待分支优先于覆盖门 ──

    [TestMethod]
    public async Task EngineeringGatesContract_WithFailedCheck_FailedBranchWins()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            PureEngineeringSource,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report(
                    "build", GoalVerificationSpecKinds.Build,
                    status: GoalCriterionResultStatuses.Failed,
                    failureCode: "non_zero_exit_code",
                    message: "dotnet build exited with code 1."),
                Report("test"),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("criterion_failed", decision.BlockerCode);
    }

    [TestMethod]
    public async Task EngineeringGatesContract_WithWaitingCheck_PendingBranchWins()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            PureEngineeringSource,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports:
            [
                Report("build", GoalVerificationSpecKinds.Build),
                Report(
                    "test",
                    status: GoalCriterionResultStatuses.Waiting,
                    failureCode: "check_timeout",
                    message: "The check did not finish before its deadline."),
            ]));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("check_results_pending", decision.BlockerCode);
    }

    // ── 判定表 E：空合同/无检查定义 ⇒ 既有受阻码不变 ──

    [TestMethod]
    public async Task NoContract_KeepsAcceptanceContractMissing()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            acceptanceContractSource: null,
            criteria: [],
            checks: [],
            reports: []));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
    }

    [TestMethod]
    public async Task EngineeringGatesContract_WithoutCheckDefinitions_KeepsCheckContractMissing()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            PureEngineeringSource,
            criteria: RequiredCriteria,
            checks: [],
            reports: []));

        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("check_contract_missing", decision.BlockerCode);
    }

    // ── 判定表 F：Task-bound 且未达 goal scope ⇒ work_unit 全绿只推进，覆盖门不参与 ──

    [TestMethod]
    public async Task EngineeringGatesContract_WorkUnitPassed_StillAdvances()
    {
        var decision = await Verifier.VerifyAsync(Capsule(
            PureEngineeringSource,
            taskStatus: "InProgress",
            remainingWorkUnits: null,
            criteria: RequiredCriteria,
            checks: RequiredChecks,
            reports: CleanReports));

        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.IsNull(decision.BlockerCode);
        Assert.AreEqual(
            GoalSettlementDispositions.Advance,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }

    // ── S1-a-1/S1-a-2：来源字段的精确匹配语义与候选→胶囊透传 ──

    [TestMethod]
    public void IsEngineeringGatesOnly_MatchesExactBoundedPlanningSourceOnly()
    {
        Assert.IsTrue(Capsule("bounded_planning").IsEngineeringGatesOnly);
        Assert.IsFalse(Capsule("bounded_planning:objective_evidence").IsEngineeringGatesOnly);
        Assert.IsFalse(Capsule(null).IsEngineeringGatesOnly);
        Assert.IsFalse(Capsule("planner").IsEngineeringGatesOnly);
    }

    [TestMethod]
    public void Candidate_ToCapsule_CarriesAcceptanceContractSource()
    {
        var candidate = new GoalSettlementCandidate
        {
            GoalIterationId = "gi-1",
            GoalRunId = "goal-gate-1",
            WorkspaceId = "ws-1",
            ConversationId = "conv-1",
            AgentInstanceId = "agent-1",
            ActivationEpoch = 1,
            AggregateVersion = 0,
            IterationNo = 1,
            MaxIterations = 8,
            IterationsStarted = 1,
            Objective = "交付业务功能并提供版本化证据",
            ObjectiveVersion = 1,
            TurnId = "turn-gate-1",
            TerminalKind = "completed",
            TerminalSequence = 7,
            EvidenceRefs = ["turn:turn-gate-1:terminal:7"],
            AcceptanceContractSource = PureEngineeringSource,
        };

        var capsule = candidate.ToCapsule();

        Assert.AreEqual(PureEngineeringSource, capsule.AcceptanceContractSource);
        Assert.IsTrue(capsule.IsEngineeringGatesOnly);
    }

    [TestMethod]
    public void Candidate_ToCapsule_WithoutContractSource_LeavesFieldNull()
    {
        var candidate = new GoalSettlementCandidate
        {
            GoalIterationId = "gi-1",
            GoalRunId = "goal-gate-1",
            WorkspaceId = "ws-1",
            ConversationId = "conv-1",
            AgentInstanceId = "agent-1",
            ActivationEpoch = 1,
            AggregateVersion = 0,
            IterationNo = 1,
            MaxIterations = 8,
            IterationsStarted = 1,
            Objective = "交付业务功能并提供版本化证据",
            ObjectiveVersion = 1,
            TurnId = "turn-gate-1",
            TerminalKind = "completed",
            TerminalSequence = 7,
            EvidenceRefs = ["turn:turn-gate-1:terminal:7"],
        };

        var capsule = candidate.ToCapsule();

        Assert.IsNull(capsule.AcceptanceContractSource);
        Assert.IsFalse(capsule.IsEngineeringGatesOnly);
    }
}
