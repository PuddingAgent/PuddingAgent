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
            // 2026-11-14：聚合逻辑收敛到 SubAgentLatencyAggregator，与 pool_action=logs 共用同一实现。
            // 两处各写一份必然漂移（例如轮次到底数 llm.completed 还是 round.completed）。
            var aggregator = new SubAgentLatencyAggregator();
            foreach (var line in await File.ReadAllLinesAsync(eventsPath, ct))
            {
                ct.ThrowIfCancellationRequested();
                aggregator.AddEventLine(line);
            }

            return aggregator.Build(runId);
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

    /// <summary>
    /// 定位 run 目录。
    /// <para>
    /// 2026-11-14：原先用 <c>EnumerateFiles(workspacesRoot, "events.jsonl", AllDirectories)</c> 全量枚举——
    /// 为了定位**一个**目录，得遍历整个数据根下的所有文件。
    /// 目录布局由 <c>PuddingDataPaths.SubAgentRunRoot</c> 单点确定：
    /// <c>workspaces/&lt;ws&gt;/agents/&lt;agent&gt;/runs/&lt;runId&gt;</c>，而 runId 就是最后一级目录名，
    /// 所以只需三层目录列举即可命中，不碰任何文件。
    /// </para>
    /// </summary>
    private string? FindRunDirectory(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var workspacesRoot = _paths.WorkspacesRoot;
        if (!Directory.Exists(workspacesRoot)) return null;

        foreach (var workspace in SafeGetDirectories(workspacesRoot))
        {
            var agentsRoot = Path.Combine(workspace, "agents");
            if (!Directory.Exists(agentsRoot))
                continue;

            foreach (var agentDir in SafeGetDirectories(agentsRoot))
            {
                var candidate = Path.Combine(agentDir, "runs", runId);
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>目录列举失败（权限/竞争）时当作空，不让诊断整体失败。</summary>
    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
