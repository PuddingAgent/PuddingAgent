using PuddingCode.Goals;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// ADR-092 §5.1/§6.2（G92-1）：持久验收合同/检查记录读取链必须 fail-closed。
/// 这些用例固定"绝不把缺失伪造成 passed"这条生产红线：
/// 非法 JSON、未 finished、无报告 JSON 一律不构成证据。
/// </summary>
[TestClass]
public sealed class GoalVerificationPersistenceTests
{
    private static GoalCheckRecordEntity Record(string status, string? reportJson = null) => new()
    {
        CheckRecordId = "gchk-goal-1-1-3-check-1",
        DedupKey = "work_unit|2|definition-hash|fingerprint",
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        IterationNo = 3,
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 2,
        DefinitionHash = "definition-hash",
        InputFingerprint = "fingerprint",
        Status = status,
        ReportJson = reportJson,
        Attempt = 1,
        Priority = 0,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static GoalCheckReport PassedReport() => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 2,
        Status = GoalCriterionResultStatuses.Passed,
        EvidenceRefs = ["check:check-1:report:artifact-9"],
        InputFingerprint = "fingerprint",
        DefinitionHash = "definition-hash",
        ReportRef = "artifact://goal-check-report-9",
        ExitCode = 0,
        ExecutedTestCount = 7,
        PassedTestCount = 7,
        FailedTestCount = 0,
        HasUnfinishedBackgroundProcess = false,
        RunnerId = "goal-check-runner",
        InvocationId = "invocation-9",
        ReportedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [TestMethod]
    public void ReadCriteria_MissingOrBlank_ReturnsEmptyContract()
    {
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria(null).Count);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria("").Count);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria("   ").Count);
    }

    [TestMethod]
    public void ReadCriteria_MalformedJson_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria("{not-json").Count);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria("{}").Count);
    }

    [TestMethod]
    public void ReadChecks_MalformedJson_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.AreEqual(0, GoalVerificationPersistence.ReadChecks("[{]").Count);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadChecks(null).Count);
    }

    [TestMethod]
    public void ReadReports_PendingOrLeasedRecords_AreNotEvidence()
    {
        var records = new[]
        {
            Record(GoalCheckRecordStatuses.Pending),
            Record(GoalCheckRecordStatuses.Leased),
        };

        Assert.AreEqual(0, GoalVerificationPersistence.ReadReports(records).Count);
    }

    [TestMethod]
    public void ReadReports_FinishedWithoutReportJson_IsNotEvidence()
    {
        var records = new[] { Record(GoalCheckRecordStatuses.Finished) };

        // 关键红线：finished 但没有真实运行报告时，必须等同"未运行"，不得当成通过。
        Assert.AreEqual(0, GoalVerificationPersistence.ReadReports(records).Count);
    }

    [TestMethod]
    public void ReadReports_FinishedWithMalformedReportJson_IsNotEvidence()
    {
        var records = new[] { Record(GoalCheckRecordStatuses.Finished, "{oops") };

        Assert.AreEqual(0, GoalVerificationPersistence.ReadReports(records).Count);
    }

    [TestMethod]
    public void ReadReports_FinishedWithReportJson_PreservesRunnerInvocationAndTestCounts()
    {
        var json = GoalVerificationPersistence.SerializeReport(PassedReport());
        var records = new[] { Record(GoalCheckRecordStatuses.Finished, json) };

        var reports = GoalVerificationPersistence.ReadReports(records);

        Assert.AreEqual(1, reports.Count);
        var report = reports[0];
        Assert.AreEqual("check-1", report.CheckId);
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, report.Status);
        Assert.AreEqual("goal-check-runner", report.RunnerId);
        Assert.AreEqual("invocation-9", report.InvocationId);
        Assert.AreEqual(0, report.ExitCode);
        Assert.AreEqual(7, report.ExecutedTestCount);
        Assert.AreEqual(7, report.PassedTestCount);
        Assert.AreEqual(0, report.FailedTestCount);
        Assert.AreEqual(false, report.HasUnfinishedBackgroundProcess);
    }

    [TestMethod]
    public void BuildDedupKey_IsScopedByRevisionAndFingerprint()
    {
        var baseline = GoalVerificationPersistence.BuildDedupKey("work_unit", 1, "hash", "fingerprint");

        Assert.AreEqual(
            baseline,
            GoalVerificationPersistence.BuildDedupKey("work_unit", 1, "hash", "fingerprint"));
        Assert.AreNotEqual(
            baseline,
            GoalVerificationPersistence.BuildDedupKey("goal", 1, "hash", "fingerprint"));
        Assert.AreNotEqual(
            baseline,
            GoalVerificationPersistence.BuildDedupKey("work_unit", 2, "hash", "fingerprint"));
        Assert.AreNotEqual(
            baseline,
            GoalVerificationPersistence.BuildDedupKey("work_unit", 1, "hash", "other-fingerprint"));
    }

    [TestMethod]
    public void BuildContractIdAndCheckRecordId_AreStableKeys()
    {
        Assert.AreEqual("gc-goal-1-1-7", GoalVerificationPersistence.BuildContractId("goal-1", 1, 7));
        Assert.AreEqual(
            "gchk-goal-1-1-3-check-1",
            GoalVerificationPersistence.BuildCheckRecordId("goal-1", 1, 3, "check-1"));
    }
}
