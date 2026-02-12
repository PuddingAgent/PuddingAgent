using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent 自我诊断工具 — 让 Agent 读取自身的运行时指标，
/// 实现自我观察 → 自我优化的反馈闭环。
///
/// 支持三种诊断模式：
///   - tool_stats: 查询指定工具的调用统计（成功率、耗时、常见错误）
///   - slowest_tools: 列出最慢的 N 个工具
///   - cache_health: 查询缓存命中率和 prefix churn 来源
/// </summary>
[Tool(
    id: "agent_diagnostics",
    name: "Agent diagnostics",
    description: "Query this agent's runtime metrics: tool call success/failure rates, average durations, cache health, and more. Use this to self-diagnose and optimize your own tool usage.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class AgentDiagnosticsTool : PuddingToolBase<AgentDiagnosticsArgs>
{
    private readonly IRuntimeActivitySink? _activitySink;
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentDiagnosticsTool(
        IRuntimeActivitySink? activitySink,
        IServiceScopeFactory scopeFactory)
    {
        _activitySink = activitySink;
        _scopeFactory = scopeFactory;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentDiagnosticsArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action ?? "";
        var toolName = args.ToolName;
        var limit = Math.Clamp(args.Limit ?? 20, 1, 200);
        var sessionId = args.SessionId ?? context.SessionId;

        var result = action switch
        {
            "tool_stats" => await GetToolStatsAsync(toolName ?? "", limit, ct),
            "slowest_tools" => await GetSlowestToolsAsync(limit, ct),
            "cache_health" => await GetCacheHealthAsync(sessionId, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unknown action '{action}'. Valid: tool_stats, slowest_tools, cache_health." })
        };

        return ToolExecutionResult.Ok(result);
    }

    // ── Diagnostic queries ─────────────────────────────────────────

    private async Task<string> GetToolStatsAsync(string toolName, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return JsonSerializer.Serialize(new { error = "tool_name is required for tool_stats action." });

        if (_activitySink is null)
            return JsonSerializer.Serialize(new { error = "Runtime activity sink is not available in this environment." });

        var query = new RuntimeActivityQuery
        {
            Component = null, // query all components — Smart* tools use SmartToolWrapper, traditional tools use ToolRunner
            Limit = Math.Max(limit * 10, 500)
        };

        var activities = await _activitySink.QueryAsync(query, ct);

        if (activities.Count == 0)
            return JsonSerializer.Serialize(new
            {
                tool_name = toolName,
                total_calls = 0,
                hint = "No RuntimeActivity records exist for any tool. The activity database may have been cleared on restart (in-memory storage) or no tool calls have been recorded yet since the last deployment.",
                diagnostics = new { total_activities_in_db = 0, query_component = RuntimeActivityComponents.ToolRunner }
            });

        var toolActivities = activities
            .Where(a =>
                (a.Metadata is not null && a.Metadata.TryGetValue("tool_name", out var name) &&
                 name.Contains(toolName, StringComparison.OrdinalIgnoreCase))
                || (a.Operation is not null && a.Operation.Contains(toolName, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();

        if (toolActivities.Count == 0)
        {
            var availableTools = activities
                .Where(a => a.Metadata is not null && a.Metadata.TryGetValue("tool_name", out _))
                .Select(a => a.Metadata!["tool_name"])
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .Take(30)
                .ToList();
            return JsonSerializer.Serialize(new
            {
                tool_name = toolName,
                total_calls = 0,
                hint = $"Tool '{toolName}' not found in {activities.Count} RuntimeActivity records.",
                diagnostics = new
                {
                    total_activities_in_db = activities.Count,
                    query_component = RuntimeActivityComponents.ToolRunner,
                    available_tool_names = availableTools
                }
            });
        }

        var successCount = toolActivities.Count(a =>
            string.Equals(a.Status, RuntimeActivityStatuses.Succeeded, StringComparison.OrdinalIgnoreCase));
        var failCount = toolActivities.Count(a =>
            string.Equals(a.Status, RuntimeActivityStatuses.Failed, StringComparison.OrdinalIgnoreCase));
        var avgDuration = toolActivities.Average(a => a.DurationMs ?? 0);
        var maxDuration = toolActivities.Max(a => a.DurationMs ?? 0);

        var errors = toolActivities
            .Where(a => !string.IsNullOrWhiteSpace(a.ErrorMessage))
            .GroupBy(a => TruncateError(a.ErrorMessage!))
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new { message = g.Key, count = g.Count() })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            tool_name = toolName,
            total_calls = toolActivities.Count,
            success_count = successCount,
            failure_count = failCount,
            success_rate = toolActivities.Count > 0
                ? Math.Round((double)successCount / toolActivities.Count, 3)
                : 0,
            avg_duration_ms = Math.Round(avgDuration, 1),
            max_duration_ms = maxDuration,
            common_errors = errors
        });
    }

    private async Task<string> GetSlowestToolsAsync(int limit, CancellationToken ct)
    {
        if (_activitySink is null)
            return JsonSerializer.Serialize(new { error = "Runtime activity sink is not available in this environment." });

        var query = new RuntimeActivityQuery
        {
            Component = null, // query all components — Smart* tools use SmartToolWrapper, traditional tools use ToolRunner
            Limit = 500
        };

        var activities = await _activitySink.QueryAsync(query, ct);

        var slowestTools = activities
            .Where(a => a.DurationMs.HasValue && !string.IsNullOrWhiteSpace(a.Operation))
            .GroupBy(a => a.Metadata is not null && a.Metadata.TryGetValue("tool_name", out var tn) ? tn : a.Operation!)
            .Select(g => new
            {
                tool = g.Key,
                avg_ms = Math.Round(g.Average(a => a.DurationMs!.Value), 1),
                max_ms = g.Max(a => a.DurationMs!.Value),
                calls = g.Count()
            })
            .OrderByDescending(x => x.avg_ms)
            .Take(limit)
            .ToList();

        if (slowestTools.Count == 0)
            return JsonSerializer.Serialize(new
            {
                slowest_tools = new object[0],
                hint = "No tool duration data available. The activity sink may not have tool_runner events yet."
            });

        return JsonSerializer.Serialize(new { slowest_tools = slowestTools });
    }

    private async Task<string> GetCacheHealthAsync(string? sessionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var cacheSvc = scope.ServiceProvider.GetService<CacheDiagnosticsService>();

        if (cacheSvc is null)
            return JsonSerializer.Serialize(new { error = "Cache diagnostics service is not available." });

        if (string.IsNullOrWhiteSpace(sessionId))
            return JsonSerializer.Serialize(new
            {
                error = "session_id is required for cache_health action.",
                hint = "Use your current session id from the RUNTIME layer (Session field)."
            });

        try
        {
            var report = await cacheSvc.GetSessionReportAsync(sessionId, limit: 50, ct);

            return JsonSerializer.Serialize(new
            {
                session_id = report.SessionId,
                analyzed_events = report.AnalyzedEventCount,
                distinct_prefix_hashes = report.DistinctPrefixHashCount,
                status = report.Status,
                average_cache_hit_rate = report.AverageCacheHitRate,
                cache_hit_tokens = report.CacheHitTokens,
                cache_miss_tokens = report.CacheMissTokens,
                cache_eligible_tokens = report.CacheEligibleTokens,
                first_churn_reason = report.FirstChurnReason,
                first_churn_source = report.FirstChurnSource,
                turns = report.Turns.Take(10).Select(t => new
                {
                    source_type = t.SourceType,
                    source_id = t.SourceId,
                    prefix_hash = t.PrefixHash?[..Math.Min(12, t.PrefixHash.Length)],
                    hit_rate = t.CacheHitRate,
                    cost = t.TotalCost
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Failed to query cache health.",
                detail = ex.Message
            });
        }
    }

    private static string TruncateError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "(empty)";
        return message.Length > 120 ? message[..120] + "..." : message;
    }
}

public sealed record AgentDiagnosticsArgs
{
    [ToolParam("diagnostics mode: tool_stats, slowest_tools, or cache_health")]
    public string? Action { get; init; }

    [ToolParam("tool name to query (for tool_stats action)")]
    public string? ToolName { get; init; }

    [ToolParam("number of results (default: 20, max: 200)")]
    public int? Limit { get; init; }

    [ToolParam("session id (for cache_health action; default: current session)")]
    public string? SessionId { get; init; }
}
