using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;

namespace PuddingPlatform.Services;

/// <summary>
/// ISubAgentDiagnosticsService 的文件系统实现。
/// 默认只读 run.json manifest 文件，不加载 events.jsonl，保证性能。
/// 单个 run.json 解析失败不中断整体扫描。
/// 例外（2026-11-14）：run.json 只在终态才带 TotalRounds / TotalToolCalls，
/// 因此仅在「字段缺失且状态为 running」时，用 <see cref="SubAgentRunProgressReader"/> 从增量日志现算；
/// 终态运行仍然只读 run.json，本类的性能约定不变。
/// </summary>
public sealed class SubAgentDiagnosticsService : ISubAgentDiagnosticsService
{
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<SubAgentDiagnosticsService> _logger;

    public SubAgentDiagnosticsService(
        PuddingDataPaths paths,
        ILogger<SubAgentDiagnosticsService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SubAgentDiagnosticsReport> GetDiagnosticsAsync(
        SubAgentDiagnosticsRequest request,
        CancellationToken ct = default)
    {
        var runsRoot = Path.Combine(
            _paths.WorkspaceAgentRoot(request.WorkspaceId, request.AgentInstanceId),
            "runs");

        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, request.HoursBack));
        var maxRuns = Math.Clamp(request.MaxRuns, 1, 10_000);
        var now = DateTimeOffset.UtcNow;

        var summaries = new List<SubAgentRunSummary>();

        if (Directory.Exists(runsRoot))
        {
            foreach (var runDir in Directory.GetDirectories(runsRoot))
            {
                ct.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(runDir);
                if (!dirName.StartsWith("run_", StringComparison.Ordinal))
                    continue;

                var runJsonPath = Path.Combine(runDir, "run.json");
                if (!File.Exists(runJsonPath))
                    continue;

                SubAgentRunManifest? manifest;
                try
                {
                    var json = await File.ReadAllTextAsync(runJsonPath, ct);
                    manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
                        json, PuddingJsonContracts.PrettyJson);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    _logger.LogWarning(
                        ex,
                        "[SubAgentDiagnostics] Skipping invalid run manifest path={Path}",
                        runJsonPath);
                    continue;
                }

                if (manifest is null)
                    continue;

                // 按 startedAt 过滤时间窗口
                if (manifest.StartedAt < cutoff)
                    continue;

                var durationMs = (long)((manifest.CompletedAt ?? now) - manifest.StartedAt).TotalMilliseconds;

                // 2026-11-14：run.json 只在终态写 TotalRounds/TotalToolCalls，运行中为 null。
                // 旧实现直接 ?? 0，导致「正在跑 12 轮」的子代理对外报「0 轮」。
                // 只在缺字段且状态为 running 时读增量日志——终态运行不付这个代价。
                var totalRounds = manifest.TotalRounds;
                var totalToolCalls = manifest.TotalToolCalls;
                if ((totalRounds is null || totalToolCalls is null)
                    && string.Equals(manifest.Status, "running", StringComparison.OrdinalIgnoreCase)
                    && SubAgentRunProgressReader.TryRead(runDir) is { } progress)
                {
                    totalRounds ??= progress.Rounds;
                    totalToolCalls ??= progress.ToolCalls;
                }

                summaries.Add(new SubAgentRunSummary
                {
                    RunId = manifest.RunId,
                    Status = manifest.Status,
                    Role = manifest.Role,
                    OriginToolId = manifest.OriginToolId,
                    ModelId = manifest.ModelId,
                    StartedAt = manifest.StartedAt,
                    CompletedAt = manifest.CompletedAt,
                    DurationMs = Math.Max(0, durationMs),
                    TotalRounds = totalRounds ?? 0,
                    TotalToolCalls = totalToolCalls ?? 0,
                    ErrorMessage = manifest.ErrorMessage,
                });

                if (summaries.Count >= maxRuns)
                    break;
            }
        }

        // 按 startedAt 降序排列
        summaries.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));

        var overall = ComputeRoleStats("__all__", summaries);
        var byRole = summaries
            .GroupBy(s => s.Role ?? "(none)")
            .Select(g => ComputeRoleStats(g.Key, g.ToList()))
            .OrderByDescending(r => r.TotalRuns)
            .ToList();
        var byModel = summaries
            .GroupBy(s => s.ModelId ?? "(unknown)")
            .Select(g => new SubAgentModelStats
            {
                ModelId = g.Key,
                Stats = ComputeRoleStats(g.Key, g.ToList()),
            })
            .OrderByDescending(m => m.Stats.TotalRuns)
            .ToList();

        return new SubAgentDiagnosticsReport
        {
            GeneratedAt = now,
            Request = request,
            Overall = overall,
            ByRole = byRole,
            ByModel = byModel,
            RecentRuns = summaries.Take(50).ToList(),
        };
    }

    /// <summary>
    /// 生成诊断报告的文本摘要，末尾包含生成时间戳。
    /// </summary>
    public async Task<string> GetDiagnosticsReportAsync(
        SubAgentDiagnosticsRequest request,
        CancellationToken ct = default)
    {
        var diagnostics = await GetDiagnosticsAsync(request, ct);

        var report = $"=== Sub-Agent Diagnostics Report ===\n";
        report += $"Workspace: {request.WorkspaceId}\n";
        report += $"Agent: {request.AgentInstanceId}\n";
        report += $"Hours Back: {request.HoursBack}\n";
        report += $"Total Runs: {diagnostics.Overall.TotalRuns}\n";
        report += $"Success: {diagnostics.Overall.SuccessCount}, Failed: {diagnostics.Overall.FailedCount}\n";
        report += $"Avg Duration: {diagnostics.Overall.AvgDurationMs}ms\n";
        report += $"P50 Duration: {diagnostics.Overall.P50DurationMs}ms\n";
        report += $"P95 Duration: {diagnostics.Overall.P95DurationMs}ms\n";

        // 失败原因分类分布
        var o = diagnostics.Overall;
        var totalClassified = o.FailureTimeoutCount + o.ToolErrorCount + o.LlmErrorCount + o.GuardrailBlockedCount + o.UnknownErrorCount;
        if (totalClassified > 0)
        {
            report += $"\n--- Failure Classification ---\n";
            report += $"  timeout: {o.FailureTimeoutCount} → {GetFailureSuggestion("timeout")}\n";
            report += $"  tool_error: {o.ToolErrorCount} → {GetFailureSuggestion("tool_error")}\n";
            report += $"  llm_error: {o.LlmErrorCount} → {GetFailureSuggestion("llm_error")}\n";
            report += $"  guardrail_blocked: {o.GuardrailBlockedCount} → {GetFailureSuggestion("guardrail_blocked")}\n";
            report += $"  unknown: {o.UnknownErrorCount} → {GetFailureSuggestion("unknown")}\n";
        }

        if (diagnostics.ByRole.Count > 0)
        {
            report += "\n--- By Role ---\n";
            foreach (var role in diagnostics.ByRole)
            {
                report += $"  {role.Role}: {role.TotalRuns} runs, {role.SuccessCount} ok, {role.FailedCount} failed\n";
            }
        }

        if (diagnostics.ByModel.Count > 0)
        {
            report += "\n--- By Model ---\n";
            foreach (var model in diagnostics.ByModel)
            {
                report += $"  {model.ModelId}: {model.Stats.TotalRuns} runs\n";
            }
        }

        report += $"\n--- Report generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---";

        return report;
    }

    /// <summary>
    /// 获取最近 count 个 run 的 RunId 列表，方便调用方只获取 run ID 而不需要完整报告。
    /// </summary>
    public async Task<List<string>> GetRecentRunIds(SubAgentDiagnosticsRequest request, int count = 5, CancellationToken ct = default)
    {
        var report = await GetDiagnosticsAsync(request, ct);
        return report.RecentRuns.Take(count).Select(r => r.RunId).ToList();
    }

    private static SubAgentRoleStats ComputeRoleStats(string role, List<SubAgentRunSummary> runs)
    {
        if (runs.Count == 0)
        {
            return new SubAgentRoleStats
            {
                Role = role,
                TotalRuns = 0,
            };
        }

        var durations = runs.Select(r => (double)r.DurationMs).OrderBy(d => d).ToList();

        // 失败原因分类（仅针对有 ErrorMessage 的失败/中断 run）
        var failedRuns = runs.Where(r => r.Status is "failed" or "interrupted" && !string.IsNullOrEmpty(r.ErrorMessage)).ToList();
        int failureTimeout = 0, toolError = 0, llmError = 0, guardrailBlocked = 0, unknownError = 0;
        foreach (var run in failedRuns)
        {
            var msg = run.ErrorMessage!;
            if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                failureTimeout++;
            else if (msg.Contains("tool", StringComparison.OrdinalIgnoreCase))
                toolError++;
            else if (msg.Contains("llm", StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("api", StringComparison.OrdinalIgnoreCase))
                llmError++;
            else if (msg.Contains("guard", StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("block", StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("safety", StringComparison.OrdinalIgnoreCase))
                guardrailBlocked++;
            else
                unknownError++;
        }

        return new SubAgentRoleStats
        {
            Role = role,
            TotalRuns = runs.Count,
            SuccessCount = runs.Count(r => r.Status == "completed"),
            FailedCount = runs.Count(r => r.Status is "failed" or "interrupted"),
            CancelledCount = runs.Count(r => r.Status == "cancelled"),
            TimeoutCount = runs.Count(r => r.Status == "timed_out"),
            FailureTimeoutCount = failureTimeout,
            ToolErrorCount = toolError,
            LlmErrorCount = llmError,
            GuardrailBlockedCount = guardrailBlocked,
            UnknownErrorCount = unknownError,
            AvgDurationMs = Math.Round(durations.Average(), 1),
            P50DurationMs = Math.Round(Percentile(durations, 0.50), 1),
            P95DurationMs = Math.Round(Percentile(durations, 0.95), 1),
            AvgRounds = Math.Round(runs.Average(r => (double)r.TotalRounds), 1),
            AvgToolCalls = Math.Round(runs.Average(r => (double)r.TotalToolCalls), 1),
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        var index = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];

        var weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    /// <inheritdoc />
    public async Task<SubAgentLatencyBreakdown?> GetRunLatencyBreakdownAsync(
        string runId, CancellationToken ct = default)
    {
        // 从 run.json 获取 workspace/agent
        var runDir = FindRunDirectory(runId);
        if (runDir is null) return null;

        var eventsPath = Path.Combine(runDir, "events.jsonl");
        if (!File.Exists(eventsPath)) return null;

        try
        {
            var lines = await File.ReadAllLinesAsync(eventsPath, ct);
            long llmMs = 0;
            long toolMs = 0;
            int rounds = 0;
            int toolCalls = 0;
            long? firstTs = null;
            long? lastTs = null;

            // 跟踪每轮的 LLM started 时间戳
            long? currentLlmStart = null;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    // 2026-11-14：事件行的实际字段是 eventType（不是 type），
                    // 且 duration_ms 在 payload 里而不是根上。旧代码两条都读错，
                    // 于是本方法对任何 run 都返回「全 0 的合法对象」——不报错，只是永远没数。
                    var type = root.TryGetProperty("eventType", out var t) ? t.GetString() : null;
                    var tsStr = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
                    if (type is null || tsStr is null) continue;
                    if (!DateTimeOffset.TryParse(tsStr, out var timestamp)) continue;
                    var epoch = timestamp.ToUnixTimeMilliseconds();

                    if (firstTs is null) firstTs = epoch;
                    lastTs = epoch;

                    switch (type)
                    {
                        case "subagent.llm.started":
                            currentLlmStart = epoch;
                            break;
                        case "subagent.llm.completed":
                            // 只做 LLM 耗时累加；轮次改由 round.completed 单点计数，避免两处定义漂移。
                            if (currentLlmStart.HasValue)
                            {
                                llmMs += Math.Max(0, epoch - currentLlmStart.Value);
                                currentLlmStart = null;
                            }
                            break;
                        case "subagent.round.completed":
                            rounds++;
                            break;
                        case "subagent.tool.completed":
                        case "subagent.tool.failed":
                            // 优先 payload.duration_ms（当前格式），兼容根级旧格式。
                            long dur = 0;
                            if (root.TryGetProperty("payload", out var payload)
                                && payload.TryGetProperty("duration_ms", out var pd)
                                && pd.TryGetInt64(out var pdv))
                            {
                                dur = pdv;
                            }
                            else if (root.TryGetProperty("duration_ms", out var rd) && rd.TryGetInt64(out var rdv))
                            {
                                dur = rdv;
                            }

                            toolMs += Math.Max(0, dur);
                            toolCalls++;
                            break;
                    }
                }
                catch { /* skip malformed */ }
            }

            var totalMs = firstTs.HasValue && lastTs.HasValue
                ? Math.Max(0, lastTs.Value - firstTs.Value)
                : 0L;
            var overheadMs = Math.Max(0, totalMs - llmMs - toolMs);

            return new SubAgentLatencyBreakdown
            {
                RunId = runId,
                TotalDurationMs = totalMs,
                LlmDurationMs = llmMs,
                ToolDurationMs = toolMs,
                OverheadMs = overheadMs,
                RoundCount = rounds,
                ToolCallCount = toolCalls,
                LlmPct = totalMs > 0 ? Math.Round((double)llmMs / totalMs * 100, 1) : 0,
                ToolPct = totalMs > 0 ? Math.Round((double)toolMs / totalMs * 100, 1) : 0,
                OverheadPct = totalMs > 0 ? Math.Round((double)overheadMs / totalMs * 100, 1) : 0,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SubAgentDiagnostics] Failed to compute latency breakdown for runId={RunId}", runId);
            return null;
        }
    }

    /// <summary>
    /// 根据失败类别返回操作建议。
    /// </summary>
    private static string GetFailureSuggestion(string category) => category switch
    {
        "timeout" => "Consider increasing timeout_seconds or splitting into smaller atomic tasks",
        "tool_error" => "Check tool permissions, parameter formats, and WHERE path accuracy",
        "llm_error" => "Check API connectivity, rate limits, and model availability",
        "guardrail_blocked" => "Review task content for safety policy violations",
        _ => "Run latency_breakdown for detailed timing analysis",
    };

    private string? FindRunDirectory(string runId)
    {
        var workspacesRoot = _paths.WorkspacesRoot;
        if (!Directory.Exists(workspacesRoot)) return null;

        foreach (var eventsFile in Directory.EnumerateFiles(workspacesRoot, "events.jsonl", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(eventsFile);
            if (dir is not null && Path.GetFileName(dir) == runId)
                return dir;
        }
        return null;
    }
}
