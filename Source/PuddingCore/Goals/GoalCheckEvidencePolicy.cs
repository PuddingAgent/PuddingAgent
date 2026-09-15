namespace PuddingCode.Goals;

/// <summary>
/// ADR-092 §5.2（G92-1）：假绿灯不得通过。把受控检查报告归一到可裁决结果——
/// 拒绝来源不可信/跨条件串号/自报 passed、exit0 但 0 tests、缺新运行报告或缺 canonical 调用引用、
/// 缺证据引用、测试计数未知或自相矛盾、旧报告（输入指纹/定义 hash/条件 revision 变化）、未结束后台进程；
/// 无法得出结论时进入等待而不是通过。本类是纯函数：不执行工具、无写权限。
/// </summary>
public static class GoalCheckEvidencePolicy
{
    public const string CheckNotRun = "check_not_run";
    public const string CheckIdentityMismatch = "check_identity_mismatch";
    public const string UntrustedReportSource = "untrusted_report_source";
    public const string DefinitionHashMissing = "definition_hash_missing";
    public const string DefinitionChanged = "definition_changed";
    public const string InputFingerprintMissing = "input_fingerprint_missing";
    public const string StaleInputFingerprint = "stale_input_fingerprint";
    public const string CriterionRevisionChanged = "criterion_revision_changed";
    public const string InvocationReferenceMissing = "invocation_reference_missing";
    public const string RunReportMissing = "run_report_missing";
    public const string EvidenceMissing = "evidence_missing";
    public const string NonZeroExitCode = "non_zero_exit_code";
    public const string TestCountUnknown = "test_count_unknown";
    public const string TestCountConflict = "test_count_conflict";
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

        // 1) 身份关联：报告必须真的属于这个检查定义，且必须由受控执行器产生。
        if (!string.Equals(report.CriterionId, check.CriterionId, StringComparison.Ordinal))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, CheckIdentityMismatch,
                $"Report belongs to criterion '{report.CriterionId}' but check '{check.CheckId}' verifies '{check.CriterionId}'.");
        }

        if (string.IsNullOrWhiteSpace(report.RunnerId))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, UntrustedReportSource,
                "The report does not identify a controlled executor; self-reported status cannot pass.");
        }

        // 2) 版本与新鲜度：定义缺失必要 hash/指纹时不得跳过校验，直接判不可通过。
        if (report.CriterionRevision != check.CriterionRevision)
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, CriterionRevisionChanged,
                $"Report revision {report.CriterionRevision} does not match criterion revision {check.CriterionRevision}.");
        }

        if (string.IsNullOrEmpty(check.DefinitionHash))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, DefinitionHashMissing,
                "The check definition has no hash, so staleness cannot be verified.");
        }

        if (!string.Equals(report.DefinitionHash, check.DefinitionHash, StringComparison.Ordinal))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, DefinitionChanged,
                "The check definition changed after this report was produced.");
        }

        if (string.IsNullOrEmpty(check.InputFingerprint))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, InputFingerprintMissing,
                "The check declares no input fingerprint, so freshness cannot be verified.");
        }

        if (!string.Equals(report.InputFingerprint, check.InputFingerprint, StringComparison.Ordinal))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Invalidated, StaleInputFingerprint,
                "The inspected inputs changed after this report was produced; the green result is stale.");
        }

        // 3) 证据引用：任何检查都必须落具体证据，禁止空证据通过。
        if (report.EvidenceRefs.Count == 0)
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, EvidenceMissing,
                "The check claims success without any evidence reference.");
        }

        var kind = check.Kind ?? string.Empty;
        var isBuildLike = string.Equals(kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal);
        var isTest = string.Equals(kind, GoalVerificationSpecKinds.Test, StringComparison.Ordinal);
        var isPostcondition = string.Equals(kind, GoalVerificationSpecKinds.Postcondition, StringComparison.Ordinal);
        var isArtifact = string.Equals(kind, GoalVerificationSpecKinds.Artifact, StringComparison.Ordinal);
        var isSemantic = string.Equals(kind, GoalVerificationSpecKinds.Semantic, StringComparison.Ordinal);
        var isExternal = string.Equals(kind, GoalVerificationSpecKinds.External, StringComparison.Ordinal);
        var isExecutedKind = isBuildLike || isTest || isPostcondition || isArtifact;

        // 4) 必须能回溯到本次真实执行：canonical 调用引用 + 本次新生成的运行报告。
        if (isExecutedKind && string.IsNullOrWhiteSpace(report.InvocationId))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, InvocationReferenceMissing,
                "The check has no canonical invocation reference for this execution.");
        }

        if (isExecutedKind && string.IsNullOrWhiteSpace(report.ReportRef))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, RunReportMissing,
                "The check has no freshly produced run report for this execution.");
        }

        // 5) 未结束的后台进程：结论不成立，进入等待（不是失败，不得据此推进）。
        if (report.HasUnfinishedBackgroundProcess == true)
        {
            return Downgrade(report, GoalCriterionResultStatuses.Waiting, BackgroundProcessRunning,
                "The check left an unfinished background process; the result is not conclusive yet.");
        }

        // 6) 退出码：build/test/postcondition 期望 0。
        if ((isBuildLike || isTest || isPostcondition)
            && (report.ExitCode is null || report.ExitCode != 0))
        {
            return Downgrade(report, GoalCriterionResultStatuses.Failed, NonZeroExitCode,
                $"The check exited with code {report.ExitCode?.ToString() ?? "unknown"}; exit 0 alone is required but not sufficient.");
        }

        // 7) test 类：必须真实执行了用例、计数自洽且满足声明的期望数量。
        if (isTest)
        {
            if (report.ExecutedTestCount is null
                || report.PassedTestCount is null
                || report.FailedTestCount is null)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, TestCountUnknown,
                    "The test counts are unknown; a report without parsed case counts cannot pass.");
            }

            var executed = report.ExecutedTestCount.Value;
            var passed = report.PassedTestCount.Value;
            var failed = report.FailedTestCount.Value;

            if (executed < 0 || passed < 0 || failed < 0)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, TestCountConflict,
                    "The test counts are negative.");
            }

            if (passed + failed != executed)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, TestCountConflict,
                    $"Test counts contradict each other: passed {passed} + failed {failed} != executed {executed}.");
            }

            if (executed == 0 || passed == 0)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, NoTestEvidence,
                    "The test check executed no test cases; zero tests cannot pass.");
            }

            if (failed > 0)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, TestsFailed,
                    $"{failed} test case(s) failed.");
            }

            if (check.ExpectedTestCount is > 0 && passed < check.ExpectedTestCount.Value)
            {
                return Downgrade(report, GoalCriterionResultStatuses.Failed, ExpectedTestsMissing,
                    $"Expected at least {check.ExpectedTestCount.Value} passing test case(s) but observed {passed}.");
            }
        }

        // 8) semantic/external 由模型或外部主体裁决：只要求证据非空（已在 3 强制）。
        if (isSemantic || isExternal || isExecutedKind)
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
