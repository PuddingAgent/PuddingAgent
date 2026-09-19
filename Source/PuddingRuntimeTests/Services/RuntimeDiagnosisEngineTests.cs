using PuddingRuntime.Services.Diagnostics;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 诊断引擎的核心契约：无证据不下结论、每条发现自带证据与阈值、结果确定可重放。
/// 这些用例是「诊断器」的回归闸门 —— 任何把未知说成健康的改动都应在此失败。
/// </summary>
[TestClass]
public sealed class RuntimeDiagnosisEngineTests
{
    [TestMethod]
    public void NoEvidence_ReturnsUnknownAndNeverHealthy()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(new DiagnosisInput());

        Assert.AreEqual(DiagnosisVerdicts.Unknown, report.Verdict);
        Assert.AreNotEqual(DiagnosisVerdicts.Healthy, report.Verdict);
        Assert.IsNotNull(Find(report, DiagnosisCodes.SamplingInsufficient));
        Assert.IsNotNull(Find(report, DiagnosisCodes.SourceUnavailable));
    }

    [TestMethod]
    public void ActivitiesWithoutToolMetadata_DoesNotClaimHealthy()
    {
        // 有活动记录但取不到工具维度指标：没有任何检查可执行，不得声称健康。
        var report = RuntimeDiagnosisEngine.Diagnose(new DiagnosisInput
        {
            TotalActivities = 50,
            WindowStartUtc = WindowStart,
            WindowEndUtc = WindowEnd,
        });

        Assert.AreEqual(DiagnosisVerdicts.Unknown, report.Verdict);
        Assert.HasCount(0, report.ChecksRun);
    }

    [TestMethod]
    public void ToolFailureRate_AtWarningThreshold_DegradesVerdict()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(Tool("search_grep", calls: 10, failures: 2)));

        var finding = Find(report, DiagnosisCodes.ToolFailureRate);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Warning, finding.Severity);
        Assert.AreEqual(DiagnosisVerdicts.Degraded, report.Verdict);
        Assert.AreEqual("0.2", finding.Evidence["failure_rate"]);
        Assert.AreEqual("10", finding.Evidence["calls"]);
        Assert.AreEqual("2", finding.Evidence["failures"]);
    }

    [TestMethod]
    public void ToolFailureRate_AtCriticalThreshold_IsCritical()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(Tool("file_write", calls: 10, failures: 5)));

        var finding = Find(report, DiagnosisCodes.ToolFailureRate);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Critical, finding.Severity);
        Assert.AreEqual(DiagnosisVerdicts.Critical, report.Verdict);
    }

    [TestMethod]
    public void ToolFailureRate_BelowMinSample_IsNotPromotedToAnomaly()
    {
        // 3 次调用全部失败 = 100%，但样本不足以下结论：
        // 必须降级为信息级发现并显式记录跳过原因，不得算作 degraded/critical。
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(Tool("file_read", calls: 3, failures: 3)));

        Assert.IsNull(Find(report, DiagnosisCodes.ToolFailureRate));
        Assert.AreEqual(DiagnosisVerdicts.Healthy, report.Verdict);
        Assert.IsTrue(
            report.ChecksSkipped.Any(s => s.Contains(DiagnosisCodes.ToolFailureRate, StringComparison.Ordinal)),
            "跳过的检查必须显式记录原因");

        var suspicion = Find(report, DiagnosisCodes.SamplingInsufficient);
        Assert.IsNotNull(suspicion, "已知的失败不能被静默丢弃");
        Assert.AreEqual(DiagnosisSeverities.Info, suspicion.Severity);
        Assert.AreEqual("3", suspicion.Evidence["failures"]);
    }

    [TestMethod]
    public void FullCoverage_NoAnomaly_IsHealthy()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(
            Tool("a", calls: 20, failures: 0, avgMs: 100),
            Tool("b", calls: 20, failures: 0, avgMs: 100)));

        Assert.AreEqual("full", report.Coverage["scope"]);
        Assert.AreEqual(DiagnosisVerdicts.Healthy, report.Verdict);
        Assert.HasCount(0, report.Findings);
    }

    [TestMethod]
    public void PartialCoverage_NoAnomaly_IsUnknownNotHealthy()
    {
        // 只有上下文维度可用、其余三个数据源查不到：已覆盖维度无异常，但不得声称整体健康。
        var report = RuntimeDiagnosisEngine.Diagnose(new DiagnosisInput
        {
            ContextUsageRatio = 0.50,
            ContextState = "Healthy",
            CacheError = "cache service unavailable",
            SubAgentError = "subagent diagnostics unavailable",
        });

        Assert.AreEqual(DiagnosisVerdicts.Unknown, report.Verdict);
        Assert.AreNotEqual(DiagnosisVerdicts.Healthy, report.Verdict);
        Assert.AreEqual("partial", report.Coverage["scope"]);
        Assert.AreEqual("available", report.Coverage["context"]);
        Assert.AreEqual("unavailable", report.Coverage["cache"]);
        Assert.AreEqual("unavailable", report.Coverage["activity"]);
        Assert.AreEqual("unavailable", report.Coverage["subagent"]);

        var partial = Find(report, DiagnosisCodes.CoveragePartial);
        Assert.IsNotNull(partial);
        StringAssert.Contains(partial.Evidence["missing_sources"], "cache");
    }

    [TestMethod]
    public void ErrorConcentration_AboveThreshold_IsReported()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(
            Tool("send_message", calls: 10, failures: 5, errors: [("connector unreachable", 4), ("timeout", 1)])));

        var finding = Find(report, DiagnosisCodes.ToolErrorConcentration);
        Assert.IsNotNull(finding);
        Assert.AreEqual("4", finding.Evidence["top_error_count"]);
        Assert.AreEqual("connector unreachable", finding.Evidence["top_error_message"]);
    }

    [TestMethod]
    public void ErrorConcentration_BelowThreshold_IsNotReported()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(
            Tool("send_message", calls: 10, failures: 5, errors: [("e1", 2), ("e2", 2), ("e3", 1)])));

        Assert.IsNull(Find(report, DiagnosisCodes.ToolErrorConcentration));
    }

    [TestMethod]
    public void LatencyOutlier_AboveFactor_IsReported()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(
            Tool("fast_a", calls: 10, failures: 0, avgMs: 10),
            Tool("fast_b", calls: 10, failures: 0, avgMs: 10),
            Tool("fast_c", calls: 10, failures: 0, avgMs: 10),
            Tool("slow_one", calls: 10, failures: 0, avgMs: 100)));

        var finding = Find(report, DiagnosisCodes.ToolLatencyOutlier);
        Assert.IsNotNull(finding);
        Assert.AreEqual("slow_one", finding.Evidence["tool_name"]);
        Assert.AreEqual("10", finding.Evidence["median_avg_duration_ms"]);
        Assert.AreEqual(DiagnosisSeverities.Info, finding.Severity);
        // 纯性能观察不构成健康状况下降。
        Assert.AreEqual(DiagnosisVerdicts.Healthy, report.Verdict);
    }

    [TestMethod]
    public void CacheHitRate_BelowWarningThreshold_IsWarning()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with { CacheHitRate = 0.40, CacheAnalyzedEvents = 30 });

        var finding = Find(report, DiagnosisCodes.CacheHitRate);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Warning, finding.Severity);
        Assert.AreEqual(DiagnosisVerdicts.Degraded, report.Verdict);
    }

    [TestMethod]
    public void CacheHitRate_BelowCriticalThreshold_IsCritical()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with { CacheHitRate = 0.10 });

        var finding = Find(report, DiagnosisCodes.CacheHitRate);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Critical, finding.Severity);
    }

    [TestMethod]
    public void CacheHitRate_Healthy_ProducesNoFinding()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with { CacheHitRate = 0.93 });

        Assert.IsNull(Find(report, DiagnosisCodes.CacheHitRate));
        Assert.AreEqual(DiagnosisVerdicts.Healthy, report.Verdict);
    }

    [TestMethod]
    public void ContextUsage_AboveWarningThreshold_IsWarning()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with
        {
            ContextUsageRatio = 0.85,
            ContextState = "Warning",
        });

        var finding = Find(report, DiagnosisCodes.ContextUsage);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Warning, finding.Severity);
        Assert.AreEqual("Warning", finding.Evidence["state"]);
    }

    [TestMethod]
    public void ContextUsage_AboveCriticalThreshold_IsCritical()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with
        {
            ContextUsageRatio = 0.95,
            ContextState = "Critical",
        });

        var finding = Find(report, DiagnosisCodes.ContextUsage);
        Assert.IsNotNull(finding);
        Assert.AreEqual(DiagnosisSeverities.Critical, finding.Severity);
        Assert.AreEqual(DiagnosisVerdicts.Critical, report.Verdict);
    }

    [TestMethod]
    public void SubAgentFailureRate_AboveThreshold_IsWarning()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with
        {
            SubAgentTotalRuns = 20,
            SubAgentSuccessRuns = 15,
            SubAgentHoursBack = 24,
        });

        var finding = Find(report, DiagnosisCodes.SubAgentFailureRate);
        Assert.IsNotNull(finding);
        Assert.AreEqual("5", finding.Evidence["failures"]);
        Assert.AreEqual("24", finding.Evidence["hours_back"]);
    }

    [TestMethod]
    public void SubAgentBudgetExhausted_Unknown_IsSkippedNotTreatedAsZero()
    {
        // 数据源未暴露预算耗尽计数时，必须记为跳过，绝不能因为「填 0」而看起来像没有问题。
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with
        {
            SubAgentTotalRuns = 10,
            SubAgentSuccessRuns = 10,
            SubAgentBudgetExhausted = null,
        });

        Assert.IsNull(Find(report, DiagnosisCodes.SubAgentBudgetExhausted));
        Assert.IsTrue(
            report.ChecksSkipped.Any(s => s.Contains(DiagnosisCodes.SubAgentBudgetExhausted, StringComparison.Ordinal)),
            "未知的预算耗尽计数必须出现在 checks_skipped 中");
    }

    [TestMethod]
    public void SubAgentBudgetExhausted_KnownNonZero_IsReported()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput() with
        {
            SubAgentTotalRuns = 10,
            SubAgentSuccessRuns = 10,
            SubAgentBudgetExhausted = 3,
        });

        var finding = Find(report, DiagnosisCodes.SubAgentBudgetExhausted);
        Assert.IsNotNull(finding);
        Assert.AreEqual("3", finding.Evidence["budget_exhausted_count"]);
    }

    [TestMethod]
    public void MissingObservationWindow_IsReportedExplicitly()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(new DiagnosisInput { TotalActivities = 5 });

        var finding = Find(report, DiagnosisCodes.ObservationWindowUnknown);
        Assert.IsNotNull(finding);
        Assert.AreEqual("unknown", report.ObservationWindow["window_start_utc"]);
        Assert.AreEqual("5", report.ObservationWindow["total_activities"]);
    }

    [TestMethod]
    public void UnavailableSource_IsReportedWithCategory()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(new DiagnosisInput { CacheError = "cache service exploded" });

        var unavailable = report.Findings
            .Where(f => f.Code == DiagnosisCodes.SourceUnavailable)
            .ToList();
        Assert.IsTrue(unavailable.Any(f => f.Evidence["category"] == "cache"));
        Assert.AreEqual(DiagnosisVerdicts.Unknown, report.Verdict);
    }

    [TestMethod]
    public void VerdictPrecedence_CriticalWinsOverWarning()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(Tool("flaky", calls: 10, failures: 2)) with
        {
            CacheHitRate = 0.10,
        });

        Assert.IsTrue(report.Findings.Any(f => f.Severity == DiagnosisSeverities.Warning));
        Assert.IsTrue(report.Findings.Any(f => f.Severity == DiagnosisSeverities.Critical));
        Assert.AreEqual(DiagnosisVerdicts.Critical, report.Verdict);
    }

    [TestMethod]
    public void Diagnose_IsDeterministic()
    {
        var input = BaseInput(
            Tool("a", calls: 12, failures: 4, errors: [("boom", 3), ("other", 1)]),
            Tool("b", calls: 10, failures: 0)) with
        {
            CacheHitRate = 0.35,
            ContextUsageRatio = 0.88,
            ContextState = "Warning",
            SubAgentTotalRuns = 8,
            SubAgentSuccessRuns = 5,
            SubAgentBudgetExhausted = 1,
        };

        var first = RuntimeDiagnosisEngine.Diagnose(input);
        var second = RuntimeDiagnosisEngine.Diagnose(input);

        Assert.AreEqual(first.Verdict, second.Verdict);
        Assert.AreEqual(Fingerprint(first), Fingerprint(second));
        Assert.AreEqual(first.Summary, second.Summary);
    }

    [TestMethod]
    public void EveryFinding_CarriesObservationAndSuggestedAction()
    {
        var report = RuntimeDiagnosisEngine.Diagnose(BaseInput(
            Tool("a", calls: 12, failures: 4, errors: [("boom", 3)])) with
        {
            CacheHitRate = 0.10,
            ContextUsageRatio = 0.95,
            ContextState = "Critical",
        });

        Assert.IsNotEmpty(report.Findings);
        foreach (var finding in report.Findings)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Observation), finding.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.SuggestedAction), finding.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Category), finding.Code);
        }
    }

    private static readonly DateTimeOffset WindowStart = DateTimeOffset.Parse("2026-09-19T00:00:00Z");
    private static readonly DateTimeOffset WindowEnd = DateTimeOffset.Parse("2026-09-19T01:00:00Z");

    /// <summary>
    /// 默认四个数据源全部可用，便于每个用例只隔离自己要验证的那一条规则。
    /// </summary>
    private static DiagnosisInput BaseInput(params DiagnosisToolMetric[] tools) => new()
    {
        TotalActivities = 100,
        WindowStartUtc = WindowStart,
        WindowEndUtc = WindowEnd,
        Tools = tools,
        CacheHitRate = 0.90,
        CacheAnalyzedEvents = 40,
        ContextUsageRatio = 0.50,
        ContextState = "Healthy",
        SubAgentTotalRuns = 10,
        SubAgentSuccessRuns = 10,
        SubAgentHoursBack = 24,
        SubAgentBudgetExhausted = 0,
    };

    private static DiagnosisToolMetric Tool(
        string name,
        int calls,
        int failures,
        double avgMs = 100,
        double maxMs = 200,
        params (string Message, int Count)[] errors)
        => new(
            name,
            calls,
            failures,
            avgMs,
            maxMs,
            errors.Select(e => new DiagnosisErrorBucket(e.Message, e.Count)).ToList());

    private static DiagnosisFinding? Find(DiagnosisReport report, string code)
        => report.Findings.FirstOrDefault(f => f.Code == code);

    private static string Fingerprint(DiagnosisReport report)
        => string.Join("|", report.Findings.Select(f => $"{f.Code}:{f.Severity}:{f.Category}"));
}
