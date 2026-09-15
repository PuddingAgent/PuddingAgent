using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;

namespace PuddingCoreTests;

/// <summary>
/// G92-1 回归（验收标准 1/4）：来源不可信、跨条件串号、自报 passed、exit0 但 0 tests、
/// 缺 canonical 调用引用/新运行报告、缺证据、测试计数未知或矛盾、旧报告（输入指纹/定义 hash/revision 变化）、
/// 未结束后台进程，都不得通过。
/// </summary>
[TestClass]
public sealed class GoalCheckEvidencePolicyTests
{
    private const string DefinitionHash = "def-1";
    private const string Fingerprint = "fp-1";

    private static GoalCheckSpec Spec(
        string kind = GoalVerificationSpecKinds.Test,
        int revision = 1,
        int? expectedTests = 10,
        string? definitionHash = DefinitionHash,
        string? fingerprint = Fingerprint,
        string? expectedEvidence = "junit report with all required cases") => new()
    {
        CheckId = $"check-{kind}",
        CriterionId = "test",
        CriterionRevision = revision,
        Kind = kind,
        DefinitionRef = "checks/regression.md#L12",
        DefinitionHash = definitionHash,
        InputRefs = ["Source/PuddingCore"],
        InputFingerprint = fingerprint ?? string.Empty,
        ExpectedEvidence = expectedEvidence,
        ExpectedTestCount = expectedTests,
    };

    private static GoalCheckReport Report(
        string kind = GoalVerificationSpecKinds.Test,
        string status = GoalCriterionResultStatuses.Passed,
        int revision = 1,
        string? definitionHash = DefinitionHash,
        string? fingerprint = Fingerprint,
        string? runnerId = "goal-check-runner",
        string? invocationId = "inv-1",
        string? reportRef = "reports/test-20260915.txt",
        IReadOnlyList<string>? evidence = null,
        int? exitCode = 0,
        int? executed = 12,
        int? passed = 12,
        int? failed = 0,
        bool? unfinishedBackgroundProcess = false) => new()
    {
        CheckId = $"check-{kind}",
        CriterionId = "test",
        CriterionRevision = revision,
        Status = status,
        DefinitionHash = definitionHash,
        InputFingerprint = fingerprint,
        RunnerId = runnerId,
        InvocationId = invocationId,
        ReportRef = reportRef,
        EvidenceRefs = evidence ?? ["report:reports/test-20260915.txt"],
        ExitCode = exitCode,
        ExecutedTestCount = executed,
        PassedTestCount = passed,
        FailedTestCount = failed,
        HasUnfinishedBackgroundProcess = unfinishedBackgroundProcess,
    };

    private static GoalCheckReport Evaluate(GoalCheckSpec spec, GoalCheckReport report)
        => GoalCheckEvidencePolicy.EvaluateOne(spec, report);

    [TestMethod]
    public void CleanTestReport_Passes()
    {
        var result = Evaluate(Spec(), Report());

        Assert.AreEqual(GoalCriterionResultStatuses.Passed, result.Status);
        Assert.IsNull(result.FailureCode);
    }

    [TestMethod]
    public void SelfReportedPassed_WithoutRunnerId_Fails()
    {
        var result = Evaluate(Spec(), Report(runnerId: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.UntrustedReportSource, result.FailureCode);
    }

    [TestMethod]
    public void ReportForAnotherCriterion_Fails()
    {
        var result = GoalCheckEvidencePolicy.EvaluateOne(
            Spec(),
            Report() with { CriterionId = "other" });

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.CheckIdentityMismatch, result.FailureCode);
    }

    [TestMethod]
    public void ExitZero_WithZeroTests_CannotPass()
    {
        var result = Evaluate(Spec(), Report(executed: 0, passed: 0, failed: 0));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.NoTestEvidence, result.FailureCode);
    }

    [TestMethod]
    public void MissingInvocationReference_Fails()
    {
        var result = Evaluate(Spec(), Report(invocationId: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.InvocationReferenceMissing, result.FailureCode);
    }

    [TestMethod]
    public void MissingRunReport_Fails()
    {
        var result = Evaluate(Spec(), Report(reportRef: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.RunReportMissing, result.FailureCode);
    }

    [TestMethod]
    public void MissingEvidence_Fails()
    {
        var result = Evaluate(Spec(), Report(evidence: []));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, result.FailureCode);
    }

    [TestMethod]
    public void EmptyExpectedEvidence_StillRequiresEvidence()
    {
        var result = Evaluate(Spec(expectedEvidence: null), Report(evidence: []));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, result.FailureCode);
    }

    [TestMethod]
    public void NonZeroExitCode_Fails()
    {
        var result = Evaluate(
            Spec(GoalVerificationSpecKinds.Build, expectedTests: null),
            Report(GoalVerificationSpecKinds.Build, exitCode: 1));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.NonZeroExitCode, result.FailureCode);
    }

    [TestMethod]
    public void StaleFingerprint_InvalidatesGreenReport()
    {
        var result = Evaluate(Spec(fingerprint: "fp-2"), Report());

        Assert.AreEqual(GoalCriterionResultStatuses.Invalidated, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.StaleInputFingerprint, result.FailureCode);
    }

    [TestMethod]
    public void MissingFingerprintOnSpec_DoesNotSkipFreshnessCheck()
    {
        var result = Evaluate(Spec(fingerprint: null), Report(fingerprint: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.InputFingerprintMissing, result.FailureCode);
    }

    [TestMethod]
    public void MissingDefinitionHashOnSpec_DoesNotSkipFreshnessCheck()
    {
        var result = Evaluate(Spec(definitionHash: null), Report(definitionHash: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.DefinitionHashMissing, result.FailureCode);
    }

    [TestMethod]
    public void DefinitionHashChanged_InvalidatesGreenReport()
    {
        var result = Evaluate(Spec(definitionHash: "def-2"), Report());

        Assert.AreEqual(GoalCriterionResultStatuses.Invalidated, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.DefinitionChanged, result.FailureCode);
    }

    [TestMethod]
    public void CriterionRevisionChanged_InvalidatesGreenReport()
    {
        var result = Evaluate(Spec(revision: 2), Report(revision: 1));

        Assert.AreEqual(GoalCriterionResultStatuses.Invalidated, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.CriterionRevisionChanged, result.FailureCode);
    }

    [TestMethod]
    public void ExpectedTestsMissing_Fails()
    {
        var result = Evaluate(Spec(expectedTests: 10), Report(executed: 7, passed: 7));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.ExpectedTestsMissing, result.FailureCode);
    }

    [TestMethod]
    public void ContradictoryTestCounts_Fail()
    {
        var result = Evaluate(Spec(), Report(executed: 12, passed: 11, failed: 0));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.TestCountConflict, result.FailureCode);
    }

    [TestMethod]
    public void NegativeTestCounts_Fail()
    {
        var result = Evaluate(Spec(), Report(executed: 12, passed: 12, failed: -1));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.TestCountConflict, result.FailureCode);
    }

    [TestMethod]
    public void UnknownFailedTestCount_Fails()
    {
        var result = Evaluate(Spec(), Report(failed: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.TestCountUnknown, result.FailureCode);
    }

    [TestMethod]
    public void FailedTests_Fail()
    {
        var result = Evaluate(Spec(), Report(executed: 12, passed: 11, failed: 1));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.TestsFailed, result.FailureCode);
    }

    [TestMethod]
    public void UnfinishedBackgroundProcess_Waits()
    {
        var result = Evaluate(Spec(), Report(unfinishedBackgroundProcess: true));

        Assert.AreEqual(GoalCriterionResultStatuses.Waiting, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.BackgroundProcessRunning, result.FailureCode);
    }

    [TestMethod]
    public void MissingReport_IsPending()
    {
        var result = GoalCheckEvidencePolicy.EvaluateOne(Spec(), null);

        Assert.AreEqual(GoalCriterionResultStatuses.Pending, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.CheckNotRun, result.FailureCode);
    }

    [TestMethod]
    public void UnsupportedKind_Fails()
    {
        var result = Evaluate(
            Spec(kind: "wishful_thinking", expectedTests: null),
            Report(kind: "wishful_thinking"));

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.UnsupportedCheckKind, result.FailureCode);
    }

    [TestMethod]
    public void SemanticCheck_WithEvidence_Passes()
    {
        var result = Evaluate(
            Spec(GoalVerificationSpecKinds.Semantic, expectedTests: null),
            Report(GoalVerificationSpecKinds.Semantic, reportRef: null, exitCode: null, executed: null, passed: null, failed: null));

        Assert.AreEqual(GoalCriterionResultStatuses.Passed, result.Status);
    }

    [TestMethod]
    public void UndeclaredReport_DoesNotProduceAnyResult()
    {
        var results = GoalCheckEvidencePolicy.Evaluate([], [Report()]);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Evaluate_DeclaredCheckWithoutReport_IsPending()
    {
        var results = GoalCheckEvidencePolicy.Evaluate([Spec()], []);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Pending, results[0].Status);
    }

    [TestMethod]
    public void Evaluate_DowngradedReport_IsNotCountedAsVerifiedAcceptance()
    {
        var results = GoalCheckEvidencePolicy.Evaluate([Spec(fingerprint: "fp-2")], [Report()]);
        var decision = new GoalVerificationDecision
        {
            Verdict = GoalVerificationVerdict.Continue,
            Reason = "evidence policy",
            EvidenceRefs = ["turn:turn-1:terminal:7"],
            Criteria = [new GoalCriterion { Id = "test", Revision = 1, Requirement = "all tests pass", Required = true }],
            CriterionResults = results.ToCriterionResults(),
        };

        Assert.IsFalse(GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision));
        Assert.AreEqual(
            GoalSettlementDispositions.ContinueCurrent,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
    }
}
