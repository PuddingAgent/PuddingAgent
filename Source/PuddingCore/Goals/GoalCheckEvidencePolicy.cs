namespace PuddingCode.Goals;

/// <summary>
/// ADR-092 §5.2（G92-1）：假绿灯不得通过。把受控检查报告归一到可裁决结果——
/// 拒绝自报 DONE、exit0 但 0 tests、缺新运行报告、缺证据引用、旧报告（输入指纹/定义 hash 变化）、
/// 未结束后台进程；无法得出结论时进入等待而不是通过。
/// 本类是纯函数，不执行任何工具、无写权限。
/// </summary>
public static class GoalCheckEvidencePolicy
{
    public const string CheckNotRun = "check_not_run";
    public const string StaleInputFingerprint = "stale_input_fingerprint";
    public const string DefinitionChanged = "definition_changed";
    public const string CriterionRevisionChanged = "criterion_revision_changed";
    public const string RunReportMissing = "run_report_missing";
    public const string EvidenceMissing = "evidence_missing";
    public const string NonZeroExitCode = "non_zero_exit_code";
    public const string NoTestEvidence = "no_test_evidence";
    public const string ExpectedTestsMissing = "expected_tests_missing";
    public const string TestsFailed = "tests_failed";
    public const string BackgroundProcessRunning = "background_process_running";
    public const string UnsupportedCheckKind = "unsupported_check_kind";

    /// <summary>按检查定义归一报告集合：结果只由“声明过的检查”产生，未声明的报告不计入通过。</summary>
    public static IReadOnlyList<GoalCheckReport> Evaluate(
        IReadOnlyList<GoalCheckSpec> checks,
        IReadOnlyList<GoalCheckReport>? reports)
    {
        if (checks is null || checks.Count == 0)
            return [];

        return checks
            .Select(check => EvaluateOne(check, FindReport(check.CheckId, reports)))
            .ToList();
    }

    /// <summary>归一单个检查：只可能把 passed 降级为 invalidated/failed/waiting，绝不把失败升级为通过。</summary>
    public static GoalCheckReport EvaluateOne(GoalCheckSpec check, GoalCheckReport? report)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (report is null)
        {
            return Pending(check, CheckNotRun, "The declared check has no result yet; wait for it instead of passing.");
        }

        if (!string.Equals(report.Status, GoalCriterionResultStatuses.Passed, StringComparison.Ordinal))
            return report;

        // 1) 版本与新鲜度：旧绿灯必须失效，不能靠旧报告复用。
        if (report.CriterionRevision != check.CriterionRevision)
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, CriterionRevisionChanged,
                $"Report revision {report.CriterionRevision} does not match criterion revision {check.CriterionRevision}.");
        }

        if (!string.IsNullOrEmpty(check.DefinitionHash)
            && !string.Equals(report.DefinitionHash, check.DefinitionHash, StringComparison.Ordinal))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, DefinitionChanged,
                "The check definition changed after this report was produced.");
        }

        if (!string.IsNullOrEmpty(check.InputFingerprint)
            && !string.Equals(report.InputFingerprint, check.InputFingerprint, StringComparison.Ordinal))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, StaleInputFingerprint,
                "The inspected inputs changed after this report was produced; the green result is stale.");
        }

        // 2) 未结束的后台进程：结论不成立，进入等待（不是失败，不得据此推进）。
        if (report.HasUnfinishedBackgroundProcess == true)
        {
            return Downgrade(report, GoalCriterionResultStatuses.Waiting, BackgroundProcessRunning,
                "The check left an unfinished background process; the result is not conclusive yet.");
        }

        var kind = check.Kind ?? string.Empty;
        var isBuildLike = string.Equals(kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal);
        var isTest = string.Equals(kind, GoalVerificationSpecKinds.Test, StringComparison.Ordinal);
        var isPostcondition = string.Equals(kind, GoalVerificationSpecKinds.Postcondition, StringComparison.Ordinal);
        var isArtifact = string.Equals(kind, GoalVerificationSpecKinds.Artifact, StringComparison.Ordinal);
        var isSemantic = string.Equals(kind, GoalVerificationSpecKinds.Semantic, StringComparison.Ordinal);
        var isExternal = string.Equals(kind, GoalVerificationSpecKinds.External, StringComparison.Ordinal);

        // 3) 证据引用：任何检查都必须落具体证据，禁止空证据通过。
        if (report.EvidenceRefs.Count == 0 && !string.IsNullOrEmpty(check.ExpectedEvidence))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, EvidenceMissing,
                "The check claims success without any evidence reference.");
        }

        // 4) build/test/postcondition/artifact 必须引用本次新生成的运行报告，禁止复用旧报告。
        if ((isBuildLike || isTest || isPostcondition || isArtifact)
            && string.IsNullOrWhiteSpace(report.ReportRef))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, RunReportMissing,
                "The check has no freshly produced run report for this execution.");
        }

        // 5) 退出码：build/test/postcondition 期望 0。
        if ((isBuildLike || isTest || isPostcondition)
            && (report.ExitCode is null || report.ExitCode != 0))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, NonZeroExitCode,
                $"The check exited with code {report.ExitCode?.ToString() ?? "unknown"}; exit 0 alone is required but not sufficient.");
        }

        // 6) test 类：必须真实执行了用例，且满足声明的期望数量，失败数为 0。
        if (isTest)
        {
            if (report.ExecutedTestCount is null or <= 0 || report.PassedTestCount is null or <= 0)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, NoTestEvidence,
                    "The test check executed no test cases; zero tests cannot pass.");
            }

            if (report.FailedTestCount is > 0)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, TestsFailed,
                    $"{report.FailedTestCount} test case(s) failed.");
            }

            if (check.ExpectedTestCount is > 0
                && (report.PassedTestCount is null || report.PassedTestCount < check.ExpectedTestCount))
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, ExpectedTestsMissing,
                    $"Expected at least {check.ExpectedTestCount} passing test case(s) but observed {report.PassedTestCount?.ToString() ?? "none"}.");
            }
        }

        // 7) semantic/external 由模型或外部主体裁决：只要求证据非空（已在 3 强制）。
        if (isSemantic || isExternal)
            return report;

        if (isBuildLike || isTest || isPostcondition || isArtifact)
            return report;

        return Downgrade(report, GoalCriterionResultStatuses.Failed, UnsupportedCheckKind,
            $"Check kind '{check.Kind}' is not a supported verification kind.");
    }

    private static GoalCheckReport? FindReport(string checkId, IReadOnlyList<GoalCheckReport>? reports)
    {
        if (reports is null || reports.Count == 0)
            return null;

        for (var index = reports.Count - 1; index >= 0; index--)
        {
            if (string.Equals(reports[index].CheckId, checkId, StringComparison.Ordinal))
                return reports[index];
        }

        return null;
    }

    private static GoalCheckReport Pending(GoalCheckSpec check, string code, string message) => new()
    {
        CheckId = check.CheckId,
        CriterionId = check.CriterionId,
        CriterionRevision = check.CriterionRevision,
        Status = GoalCriterionResultStatuses.Pending,
        InputFingerprint = check.InputFingerprint,
        FailureCode = code,
        Message = message,
    };

    private static GoalCheckReport Downgrade(
        GoalCheckReport report,
        string status,
        string code,
        string message) => report with
    {
        Status = status,
        FailureCode = code,
        Message = message,
    };
}
