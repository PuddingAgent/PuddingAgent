namespace PuddingRuntime.Services.Diagnostics;

/// <summary>诊断发现的严重度。</summary>
public static class DiagnosisSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>诊断总判定。unknown 表示「没有足够证据」，绝不等于 healthy。</summary>
public static class DiagnosisVerdicts
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Critical = "critical";
    public const string Unknown = "unknown";
}

/// <summary>稳定的诊断码，供调用方按码分支，不要依赖文案。</summary>
public static class DiagnosisCodes
{
    public const string ToolFailureRate = "tool.failure_rate";
    public const string ToolErrorConcentration = "tool.error_concentration";
    public const string ToolLatencyOutlier = "tool.latency_outlier";
    public const string CacheHitRate = "cache.hit_rate";
    public const string ContextUsage = "context.usage";
    public const string SubAgentFailureRate = "subagent.failure_rate";
    public const string SubAgentBudgetExhausted = "subagent.budget_exhausted";
    public const string SamplingInsufficient = "sampling.insufficient";
    public const string ObservationWindowUnknown = "sampling.window_unknown";
    public const string SourceUnavailable = "source.unavailable";

    /// <summary>四个数据源没有全部可用时产生：判定只代表已覆盖维度。</summary>
    public const string CoveragePartial = "coverage.partial";
}

public sealed record DiagnosisErrorBucket(string Message, int Count);

public sealed record DiagnosisToolMetric(
    string ToolName,
    int Calls,
    int Failures,
    double AvgDurationMs,
    double MaxDurationMs,
    IReadOnlyList<DiagnosisErrorBucket> TopErrors)
{
    public double FailureRate => Calls > 0 ? (double)Failures / Calls : 0;
}

/// <summary>
/// 诊断输入：全部为已经采集好的聚合量。引擎不做任何 I/O，因此可离线重复执行且结论确定。
/// </summary>
public sealed record DiagnosisInput
{
    public int TotalActivities { get; init; }
    public DateTimeOffset? WindowStartUtc { get; init; }
    public DateTimeOffset? WindowEndUtc { get; init; }

    public IReadOnlyList<DiagnosisToolMetric> Tools { get; init; } = [];

    public double? CacheHitRate { get; init; }
    public int? CacheAnalyzedEvents { get; init; }
    public string? CacheError { get; init; }

    public double? ContextUsageRatio { get; init; }
    public string? ContextState { get; init; }
    public string? ContextError { get; init; }

    public int SubAgentTotalRuns { get; init; }
    public int SubAgentSuccessRuns { get; init; }
    /// <summary>
    /// 预算耗尽的子代理运行数。null 表示数据源未暴露该计数（未知），
    /// 不允许用 0 冒充「没有发生」——那会把未知说成健康。
    /// </summary>
    public int? SubAgentBudgetExhausted { get; init; }
    public int SubAgentHoursBack { get; init; }
    public string? SubAgentError { get; init; }
}

/// <summary>一条可解释的发现：结论必须自带证据与阈值，否则不允许产生。</summary>
public sealed record DiagnosisFinding(
    string Code,
    string Severity,
    string Category,
    string Observation,
    IReadOnlyDictionary<string, string> Evidence,
    string SuggestedAction);

public sealed record DiagnosisReport(
    string Verdict,
    string Summary,
    IReadOnlyList<DiagnosisFinding> Findings,
    IReadOnlyList<string> ChecksRun,
    IReadOnlyList<string> ChecksSkipped,
    IReadOnlyDictionary<string, string> Coverage,
    IReadOnlyDictionary<string, string> ObservationWindow);

/// <summary>
/// 确定性诊断引擎：把运行时聚合指标转成「带证据的结论」。
/// 设计原则：
/// 1) 无证据不下结论——样本不足时给 unknown，绝不默认 healthy；
/// 2) 每条发现都必须携带产生它的原始数字与所用阈值；
/// 3) 纯函数，无 I/O、无时钟、无随机，便于回归对比。
/// </summary>
public static class RuntimeDiagnosisEngine
{
    /// <summary>低于此次数不对失败率下结论（避免 1 次失败 = 100% 失败率的假警报）。</summary>
    public const int MinCallsForRateClaim = 5;

    public const double ToolFailureRateWarning = 0.20;
    public const double ToolFailureRateCritical = 0.50;

    /// <summary>单一错误占该工具全部失败的比例达到此值，说明故障高度集中、可直接定位。</summary>
    public const double ErrorConcentrationRatio = 0.60;

    /// <summary>某工具平均耗时超过全体工具中位数的此倍数，视为离群。</summary>
    public const double LatencyOutlierFactor = 3.0;

    public const double CacheHitRateWarning = 0.50;
    public const double CacheHitRateCritical = 0.20;

    public const double ContextUsageWarning = 0.80;
    public const double ContextUsageCritical = 0.92;

    public const double SubAgentFailureRateWarning = 0.25;

    public static DiagnosisReport Diagnose(DiagnosisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<DiagnosisFinding>();
        var checksRun = new List<string>();
        var checksSkipped = new List<string>();

        EvaluateToolFailureRates(input, findings, checksRun, checksSkipped);
        EvaluateErrorConcentration(input, findings, checksRun, checksSkipped);
        EvaluateLatencyOutliers(input, findings, checksRun, checksSkipped);
        EvaluateCache(input, findings, checksRun, checksSkipped);
        EvaluateContext(input, findings, checksRun, checksSkipped);
        EvaluateSubAgents(input, findings, checksRun, checksSkipped);
        EvaluateUnavailableSources(input, findings);

        var hasAnyEvidence = HasAnyEvidence(input);
        if (!hasAnyEvidence)
        {
            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.SamplingInsufficient,
                DiagnosisSeverities.Info,
                "sampling",
                "没有任何可用样本：活动流为空，缓存、上下文与子代理指标均不可用，因此无法判断健康状态。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["total_activities"] = input.TotalActivities.ToString(),
                    ["tool_metric_count"] = input.Tools.Count.ToString(),
                },
                "先产生真实流量，或先确认运行环境是否支持这些数据源，再重新诊断。"));
        }

        if (input.WindowStartUtc is null || input.WindowEndUtc is null)
        {
            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.ObservationWindowUnknown,
                DiagnosisSeverities.Info,
                "sampling",
                "无法确定观测窗口：活动记录未携带可用的时间范围，结论仅代表当前缓冲区内容。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["window_start"] = input.WindowStartUtc?.ToString("O") ?? "null",
                    ["window_end"] = input.WindowEndUtc?.ToString("O") ?? "null",
                },
                "不要把这些结论表述为「过去 N 天」的结论；如需时间趋势，应先补齐按时间过滤的查询能力。"));
        }

        var coverage = BuildCoverage(input, out var missingSources);
        if (missingSources.Count > 0)
        {
            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.CoveragePartial,
                DiagnosisSeverities.Info,
                "coverage",
                $"覆盖不完整：{string.Join("、", missingSources)} 维度不可用，本次结论只代表已覆盖维度。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["missing_sources"] = string.Join(",", missingSources),
                    ["scope"] = "partial",
                },
                "补齐缺失数据源后再重新诊断；在此之前不要把本次结论当作整体健康证明。"));
        }

        // 有数据但没有一项检查真正跑起来（例如样本全部低于阈值）时，同样不得声称健康。
        var verdict = ResolveVerdict(hasAnyEvidence, checksRun.Count > 0, missingSources.Count == 0, findings);
        return new DiagnosisReport(
            verdict,
            BuildSummary(verdict, findings, input, missingSources.Count == 0),
            findings,
            checksRun,
            checksSkipped,
            coverage,
            BuildObservationWindow(input));
    }

    private static void EvaluateToolFailureRates(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        if (input.Tools.Count == 0)
        {
            checksSkipped.Add($"{DiagnosisCodes.ToolFailureRate}: 没有工具指标");
            return;
        }

        foreach (var tool in input.Tools)
        {
            if (tool.Calls < MinCallsForRateClaim)
            {
                checksSkipped.Add(
                    $"{DiagnosisCodes.ToolFailureRate}@{tool.ToolName}: 样本 {tool.Calls} 次 < 阈值 {MinCallsForRateClaim} 次");
                if (tool.Failures > 0)
                {
                    // 不能因为样本小就把已知的失败默默丢掉：以信息级发现显式列出。
                    findings.Add(new DiagnosisFinding(
                        DiagnosisCodes.SamplingInsufficient,
                        DiagnosisSeverities.Info,
                        "tool",
                        $"工具 {tool.ToolName} 出现 {tool.Failures}/{tool.Calls} 次失败，但样本低于 {MinCallsForRateClaim} 次，不足以判定失败率异常。",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["tool_name"] = tool.ToolName,
                            ["calls"] = tool.Calls.ToString(),
                            ["failures"] = tool.Failures.ToString(),
                            ["min_calls_for_rate_claim"] = MinCallsForRateClaim.ToString(),
                        },
                        "继续观察该工具的调用；样本充足后会自动进入失败率判定。"));
                }

                continue;
            }

            checksRun.Add($"{DiagnosisCodes.ToolFailureRate}@{tool.ToolName}");
            var rate = tool.FailureRate;
            if (rate < ToolFailureRateWarning)
                continue;

            var severity = rate >= ToolFailureRateCritical
                ? DiagnosisSeverities.Critical
                : DiagnosisSeverities.Warning;

            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.ToolFailureRate,
                severity,
                "tool",
                $"工具 {tool.ToolName} 失败率 {rate:P1}（{tool.Failures}/{tool.Calls}）。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool_name"] = tool.ToolName,
                    ["calls"] = tool.Calls.ToString(),
                    ["failures"] = tool.Failures.ToString(),
                    ["failure_rate"] = rate.ToString("0.###"),
                    ["threshold_warning"] = ToolFailureRateWarning.ToString("0.###"),
                    ["threshold_critical"] = ToolFailureRateCritical.ToString("0.###"),
                },
                $"检查 {tool.ToolName} 的最近失败原因，先修最高频那一条。"));
        }
    }

    private static void EvaluateErrorConcentration(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        var candidates = input.Tools.Where(t => t.Failures > 0 && t.TopErrors.Count > 0).ToList();
        if (candidates.Count == 0)
        {
            checksSkipped.Add($"{DiagnosisCodes.ToolErrorConcentration}: 没有带错误信息的失败样本");
            return;
        }

        foreach (var tool in candidates)
        {
            checksRun.Add($"{DiagnosisCodes.ToolErrorConcentration}@{tool.ToolName}");
            var top = tool.TopErrors[0];
            var ratio = (double)top.Count / tool.Failures;
            if (ratio < ErrorConcentrationRatio)
                continue;

            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.ToolErrorConcentration,
                DiagnosisSeverities.Warning,
                "tool",
                $"工具 {tool.ToolName} 的失败集中在单一原因上：{ratio:P1} 的失败来自同一条错误（{top.Count}/{tool.Failures}）。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool_name"] = tool.ToolName,
                    ["failures"] = tool.Failures.ToString(),
                    ["top_error_count"] = top.Count.ToString(),
                    ["top_error_ratio"] = ratio.ToString("0.###"),
                    ["threshold"] = ErrorConcentrationRatio.ToString("0.###"),
                    ["top_error_message"] = top.Message,
                },
                "这一条通常是可定位的确定性缺陷，优先修复而不是增加重试。"));
        }
    }

    private static void EvaluateLatencyOutliers(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        var measured = input.Tools.Where(t => t.Calls > 0 && t.AvgDurationMs > 0).ToList();
        if (measured.Count < 2)
        {
            checksSkipped.Add($"{DiagnosisCodes.ToolLatencyOutlier}: 有耗时数据的工具不足 2 个，无法比较");
            return;
        }

        var sorted = measured.Select(t => t.AvgDurationMs).OrderBy(v => v).ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        if (median <= 0)
        {
            checksSkipped.Add($"{DiagnosisCodes.ToolLatencyOutlier}: 中位耗时为 0，比较无意义");
            return;
        }

        checksRun.Add(DiagnosisCodes.ToolLatencyOutlier);

        foreach (var tool in measured)
        {
            var factor = tool.AvgDurationMs / median;
            if (factor < LatencyOutlierFactor)
                continue;

            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.ToolLatencyOutlier,
                DiagnosisSeverities.Info,
                "tool",
                $"工具 {tool.ToolName} 平均耗时 {tool.AvgDurationMs:F1} ms，是全体工具中位数 {median:F1} ms 的 {factor:F1} 倍。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool_name"] = tool.ToolName,
                    ["avg_duration_ms"] = tool.AvgDurationMs.ToString("0.#"),
                    ["max_duration_ms"] = tool.MaxDurationMs.ToString("0.#"),
                    ["median_avg_duration_ms"] = median.ToString("0.#"),
                    ["factor"] = factor.ToString("0.#"),
                    ["threshold_factor"] = LatencyOutlierFactor.ToString("0.#"),
                },
                "确认该工具是真实工作需要还是可优化；若是检索类工具，考虑限制扫描范围。"));
        }
    }

    private static void EvaluateCache(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        if (input.CacheHitRate is null)
        {
            checksSkipped.Add($"{DiagnosisCodes.CacheHitRate}: 缓存指标不可用");
            return;
        }

        checksRun.Add(DiagnosisCodes.CacheHitRate);
        var rate = input.CacheHitRate.Value;
        if (rate >= CacheHitRateWarning)
            return;

        var severity = rate < CacheHitRateCritical
            ? DiagnosisSeverities.Critical
            : DiagnosisSeverities.Warning;

        findings.Add(new DiagnosisFinding(
            DiagnosisCodes.CacheHitRate,
            severity,
            "cache",
            $"平均缓存命中率 {rate:P1}，低于告警阈值 {CacheHitRateWarning:P0}。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hit_rate"] = rate.ToString("0.###"),
                ["analyzed_events"] = input.CacheAnalyzedEvents?.ToString() ?? "unknown",
                ["threshold_warning"] = CacheHitRateWarning.ToString("0.###"),
                ["threshold_critical"] = CacheHitRateCritical.ToString("0.###"),
            },
            "检查前缀稳定性：上下文是否在轮次之间被重排或注入易变内容。"));
    }

    private static void EvaluateContext(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        if (input.ContextUsageRatio is null)
        {
            checksSkipped.Add($"{DiagnosisCodes.ContextUsage}: 上下文指标不可用");
            return;
        }

        checksRun.Add(DiagnosisCodes.ContextUsage);
        var ratio = input.ContextUsageRatio.Value;
        if (ratio < ContextUsageWarning)
            return;

        var severity = ratio >= ContextUsageCritical
            ? DiagnosisSeverities.Critical
            : DiagnosisSeverities.Warning;

        findings.Add(new DiagnosisFinding(
            DiagnosisCodes.ContextUsage,
            severity,
            "context",
            $"上下文占用率 {ratio:P1}，状态 {input.ContextState ?? "unknown"}。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["usage_ratio"] = ratio.ToString("0.###"),
                ["state"] = input.ContextState ?? "unknown",
                ["threshold_warning"] = ContextUsageWarning.ToString("0.###"),
                ["threshold_critical"] = ContextUsageCritical.ToString("0.###"),
            },
            "压缩或卸载低价值上下文，避免触发被动压缩导致信息丢失。"));
    }

    private static void EvaluateSubAgents(
        DiagnosisInput input,
        List<DiagnosisFinding> findings,
        List<string> checksRun,
        List<string> checksSkipped)
    {
        if (input.SubAgentTotalRuns <= 0)
        {
            checksSkipped.Add($"{DiagnosisCodes.SubAgentFailureRate}: 窗口内没有子代理运行记录");
        }
        else
        {
            checksRun.Add(DiagnosisCodes.SubAgentFailureRate);
            var failures = input.SubAgentTotalRuns - input.SubAgentSuccessRuns;
            var rate = (double)failures / input.SubAgentTotalRuns;
            if (rate >= SubAgentFailureRateWarning)
            {
                findings.Add(new DiagnosisFinding(
                    DiagnosisCodes.SubAgentFailureRate,
                    DiagnosisSeverities.Warning,
                    "subagent",
                    $"子代理失败率 {rate:P1}（{failures}/{input.SubAgentTotalRuns}，窗口 {input.SubAgentHoursBack} 小时）。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["total_runs"] = input.SubAgentTotalRuns.ToString(),
                        ["success_runs"] = input.SubAgentSuccessRuns.ToString(),
                        ["failures"] = failures.ToString(),
                        ["failure_rate"] = rate.ToString("0.###"),
                        ["hours_back"] = input.SubAgentHoursBack.ToString(),
                        ["threshold"] = SubAgentFailureRateWarning.ToString("0.###"),
                    },
                    "按角色与失败类型（timeout/tool_error/llm_error/guardrail）下钻，定位是派发问题还是任务本身不可完成。"));
            }
        }

        if (input.SubAgentBudgetExhausted is null)
        {
            checksSkipped.Add($"{DiagnosisCodes.SubAgentBudgetExhausted}: 数据源未暴露预算耗尽计数，无法判定");
        }
        else if (input.SubAgentBudgetExhausted > 0)
        {
            var budgetExhausted = input.SubAgentBudgetExhausted.Value;
            checksRun.Add(DiagnosisCodes.SubAgentBudgetExhausted);
            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.SubAgentBudgetExhausted,
                DiagnosisSeverities.Warning,
                "subagent",
                $"有 {budgetExhausted} 个子代理运行以预算耗尽结束，任务被终止且父级拿不到完整交付。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["budget_exhausted_count"] = budgetExhausted.ToString(),
                    ["total_runs"] = input.SubAgentTotalRuns.ToString(),
                },
                "检查任务书是否过大：单个子代理只给一个原子目标，失败后优先续跑而不是重建。"));
        }
    }

    private static void EvaluateUnavailableSources(DiagnosisInput input, List<DiagnosisFinding> findings)
    {
        AddUnavailable(findings, "cache", input.CacheError);
        AddUnavailable(findings, "context", input.ContextError);
        AddUnavailable(findings, "subagent", input.SubAgentError);

        if (input.TotalActivities == 0)
        {
            findings.Add(new DiagnosisFinding(
                DiagnosisCodes.SourceUnavailable,
                DiagnosisSeverities.Info,
                "activity",
                "运行时活动流为空，工具维度的统计与诊断全部不可用。",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["total_activities"] = "0" },
                "确认活动流是否需要按时间过滤能力，或该环境是否根本不记录活动。"));
        }
    }

    private static void AddUnavailable(List<DiagnosisFinding> findings, string category, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;

        findings.Add(new DiagnosisFinding(
            DiagnosisCodes.SourceUnavailable,
            DiagnosisSeverities.Info,
            category,
            $"数据源 {category} 查询失败：{error}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["category"] = category,
                ["detail"] = error,
            },
            "该维度的诊断结论不可用；不要把它当成「没有问题」。"));
    }

    private static bool HasAnyEvidence(DiagnosisInput input)
        => input.TotalActivities > 0
           || input.Tools.Count > 0
           || input.CacheHitRate.HasValue
           || input.ContextUsageRatio.HasValue
           || input.SubAgentTotalRuns > 0;

    private static string ResolveVerdict(
        bool hasAnyEvidence,
        bool anySubstantiveCheckRan,
        bool coverageFull,
        IReadOnlyList<DiagnosisFinding> findings)
    {
        if (!hasAnyEvidence || !anySubstantiveCheckRan)
            return DiagnosisVerdicts.Unknown;

        var actionable = findings.Where(f => f.Severity != DiagnosisSeverities.Info).ToList();
        if (actionable.Any(f => f.Severity == DiagnosisSeverities.Critical))
            return DiagnosisVerdicts.Critical;
        if (actionable.Any(f => f.Severity == DiagnosisSeverities.Warning))
            return DiagnosisVerdicts.Degraded;
        // 已覆盖的维度未见异常，但覆盖不完整时仍不得声称整体健康 ——「没查到」不等于「没问题」。
        return coverageFull ? DiagnosisVerdicts.Healthy : DiagnosisVerdicts.Unknown;
    }

    private static string BuildSummary(
        string verdict,
        IReadOnlyList<DiagnosisFinding> findings,
        DiagnosisInput input,
        bool coverageFull)
    {
        if (verdict == DiagnosisVerdicts.Unknown)
        {
            return coverageFull
                ? $"证据不足，无法判定健康状态（活动记录 {input.TotalActivities} 条，工具指标 {input.Tools.Count} 项）。"
                : $"无法判定整体健康状态：覆盖不完整，结论仅限已覆盖维度（活动记录 {input.TotalActivities} 条，工具指标 {input.Tools.Count} 项，缺失来源见 coverage）。";
        }

        var critical = findings.Count(f => f.Severity == DiagnosisSeverities.Critical);
        var warning = findings.Count(f => f.Severity == DiagnosisSeverities.Warning);
        return $"判定 {verdict}：critical {critical} 项，warning {warning} 项（观测活动 {input.TotalActivities} 条，工具指标 {input.Tools.Count} 项）。";
    }

    /// <summary>
    /// 可用性按「数据源是否查询成功」判定，而不是「是否恰好有数据」：
    /// 空结果也是有效结果，查询失败才是未知。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildCoverage(
        DiagnosisInput input,
        out List<string> missingSources)
    {
        missingSources = [];
        var coverage = new Dictionary<string, string>(StringComparer.Ordinal);

        AddCoverage(coverage, missingSources, "activity", input.TotalActivities > 0 || input.Tools.Count > 0);
        AddCoverage(coverage, missingSources, "cache", string.IsNullOrWhiteSpace(input.CacheError));
        AddCoverage(coverage, missingSources, "context", string.IsNullOrWhiteSpace(input.ContextError));
        AddCoverage(coverage, missingSources, "subagent", string.IsNullOrWhiteSpace(input.SubAgentError));

        coverage["scope"] = missingSources.Count == 0 ? "full" : "partial";
        return coverage;
    }

    private static void AddCoverage(
        Dictionary<string, string> coverage,
        List<string> missingSources,
        string name,
        bool available)
    {
        coverage[name] = available ? "available" : "unavailable";
        if (!available)
            missingSources.Add(name);
    }

    private static IReadOnlyDictionary<string, string> BuildObservationWindow(DiagnosisInput input)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window_start_utc"] = input.WindowStartUtc?.ToString("O") ?? "unknown",
            ["window_end_utc"] = input.WindowEndUtc?.ToString("O") ?? "unknown",
            ["total_activities"] = input.TotalActivities.ToString(),
            ["tool_metric_count"] = input.Tools.Count.ToString(),
            ["subagent_hours_back"] = input.SubAgentHoursBack.ToString(),
        };
}
