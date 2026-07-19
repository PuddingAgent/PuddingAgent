using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// QuerySubAgentsTool — Agent 自我查询子代理状态的工具。
/// </summary>
[Tool(
    id: "query_sub_agents",
    name: "query_sub_agents",
    description: "查询当前会话的子代理状态。支持操作：list（列出全部）、stats（统计摘要）、status {id}（查询指定子代理）、grep {keyword}（搜索）、recent {days}（时间范围）、running（仅运行中）。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low)]
public sealed class QuerySubAgentsTool : PuddingToolBase<QuerySubAgentsArgs>
{
    private readonly ISubAgentManager _mgr;
    private readonly ILogger<QuerySubAgentsTool> _logger;

    public QuerySubAgentsTool(ISubAgentManager mgr, ILogger<QuerySubAgentsTool> logger)
    {
        _mgr = mgr;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        QuerySubAgentsArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action ?? "list";
        var daysStr = args.Days?.ToString() ?? "7";

        try
        {
            return action.ToLowerInvariant() switch
            {
                "list" => await HandleListAsync(context.SessionId),
                "stats" => await HandleStatsAsync(context.SessionId),
                "status" => await HandleStatusAsync(args.SubAgentId, context.SessionId),
                "grep" => await HandleGrepAsync(args.Keyword, context.SessionId),
                "recent" => await HandleRecentAsync(daysStr, context.SessionId),
                "running" => await HandleRunningAsync(context.SessionId),
                _ => Fail($"未知操作 '{action}'。支持：list / stats / status / grep / recent / running")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QuerySubAgents] Failed action={Action} session={Session}", action, context.SessionId);
            return Fail($"查询子代理失败: {ex.Message}");
        }
    }

        private async Task<ToolExecutionResult> HandleListAsync(string sessionId)
    {
        var agents = await _mgr.GetSubAgentsAsync(sessionId);
        if (agents.Count == 0)
            return Ok("当前会话没有任何子代理记录。");

        var sb = new System.Text.StringBuilder();
        var totalTokens = agents.Sum(a => a.TokenSummary?.TotalTokens ?? 0);
        var totalCost = agents.Sum(a => a.TokenSummary?.TotalCost ?? 0);
        sb.AppendLine($"📊 子代理列表（共 {agents.Count} 个，合计 {totalTokens:N0} tokens / ${totalCost:F4}）:\n");
        foreach (var sa in agents)
        {
            var icon = sa.Status switch { "running" => "🔄", "completed" => "✅", "failed" => "❌", _ => "❓" };
            var shortId = sa.SubSessionId.Length > 20 ? "..." + sa.SubSessionId[^16..] : sa.SubSessionId;
            sb.AppendLine($"{icon} `{shortId}` [{sa.Status}]");
            sb.AppendLine($"   模板: {sa.TemplateId ?? "默认"} | 模型: {sa.ModelId ?? "默认"}");
            sb.AppendLine($"   任务: {sa.TaskSummary}");
            sb.AppendLine($"   创建: {sa.SpawnedAt:HH:mm:ss} | 完成: {sa.CompletedAt?.ToString("HH:mm:ss") ?? "-"}");
            if (sa.TokenSummary is { } ts)
            {
                var hitRate = ts.TotalTokens > 0 ? (double)ts.CacheHitTokens / ts.TotalTokens * 100 : 0;
                sb.AppendLine($"   Token: {ts.TotalTokens:N0} | 缓存命中: {hitRate:F1}% | 费用: ${ts.TotalCost:F4} | 请求: {ts.RequestCount}");
            }
        }
        return Ok(sb.ToString().Trim());
    }

        private async Task<ToolExecutionResult> HandleStatsAsync(string sessionId)
    {
        var stats = await _mgr.GetStatsAsync(sessionId);
        var agents = await _mgr.GetSubAgentsAsync(sessionId);
        var totalTokens = agents.Sum(a => a.TokenSummary?.TotalTokens ?? 0);
        var totalCost = agents.Sum(a => a.TokenSummary?.TotalCost ?? 0);
        var totalCacheHit = agents.Sum(a => a.TokenSummary?.CacheHitTokens ?? 0);
        var overallHitRate = totalTokens > 0 ? (double)totalCacheHit / totalTokens * 100 : 0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📈 子代理统计:");
        sb.AppendLine($"   总计: {stats.Total} | 运行中: {stats.Running} | 已完成: {stats.Completed} | 失败: {stats.Failed}");
        sb.AppendLine($"   Token 合计: {totalTokens:N0} | 缓存命中率: {overallHitRate:F1}% | 费用合计: ${totalCost:F4}");
        if (stats.LastCompletedId is not null) sb.AppendLine($"   最近完成: {stats.LastCompletedId[^16..]}");
        return Ok(sb.ToString().Trim());
    }

        private async Task<ToolExecutionResult> HandleStatusAsync(string? subId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(subId))
            return Fail("status 操作需要 sub_agent_id 参数");

        var status = await _mgr.GetStatusAsync(subId);
        if (status == null)
            return Fail($"未找到子代理 '{subId}'");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📋 子代理详情: {subId}");
        sb.AppendLine($"   状态: {status.Status}");
        sb.AppendLine($"   模板: {status.TemplateId ?? "默认"}");
        sb.AppendLine($"   模型: {status.ModelId ?? "默认"}");
        sb.AppendLine($"   任务: {status.TaskSummary}");
        sb.AppendLine($"   创建: {status.SpawnedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"   完成: {status.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
        if (status.ResultSummary is not null) sb.AppendLine($"   结果: {status.ResultSummary}");
        if (status.TokenSummary is { } ts)
        {
            var hitRate = ts.TotalTokens > 0 ? (double)ts.CacheHitTokens / ts.TotalTokens * 100 : 0;
            sb.AppendLine($"   ── Token 用量 ──");
            sb.AppendLine($"   总 Token: {ts.TotalTokens:N0} | 缓存命中: {ts.CacheHitTokens:N0} | 未命中: {ts.CacheMissTokens:N0}");
            sb.AppendLine($"   缓存命中率: {hitRate:F1}% | 费用: ${ts.TotalCost:F4} | 请求数: {ts.RequestCount}");
        }
        return Ok(sb.ToString().Trim());
    }

    private async Task<ToolExecutionResult> HandleGrepAsync(string? keyword, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Fail("grep 操作需要 keyword 参数");

        var results = await _mgr.GrepAsync(sessionId, keyword);
        if (results.Count == 0)
            return Ok($"未找到匹配 '{keyword}' 的子代理。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔍 搜索 '{keyword}' — 找到 {results.Count} 个结果:\n");
        foreach (var sa in results)
            sb.AppendLine($"- {sa.SubSessionId[^16..]} [{sa.Status}] {sa.TaskSummary[..Math.Min(sa.TaskSummary.Length, 60)]}");
        return Ok(sb.ToString().Trim());
    }

        private async Task<ToolExecutionResult> HandleRecentAsync(string daysStr, string sessionId)
    {
        if (!int.TryParse(daysStr, out var days) || days < 1) days = 7;
        var results = await _mgr.GetRecentAsync(sessionId, days);
        if (results.Count == 0)
            return Ok($"最近 {days} 天没有子代理记录。");

        var totalTokens = results.Sum(a => a.TokenSummary?.TotalTokens ?? 0);
        var totalCost = results.Sum(a => a.TokenSummary?.TotalCost ?? 0);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📅 最近 {days} 天的子代理（共 {results.Count} 个，合计 {totalTokens:N0} tokens / ${totalCost:F4}）:\n");
        foreach (var sa in results)
        {
            var icon = sa.Status switch { "running" => "🔄", "completed" => "✅", "failed" => "❌", _ => "❓" };
            var shortId = sa.SubSessionId.Length > 20 ? "..." + sa.SubSessionId[^16..] : sa.SubSessionId;
            sb.AppendLine($"{icon} `{shortId}` [{sa.Status}] {sa.TaskSummary[..Math.Min(sa.TaskSummary.Length, 60)]}");
            if (sa.TokenSummary is { } ts)
            {
                var hitRate = ts.TotalTokens > 0 ? (double)ts.CacheHitTokens / ts.TotalTokens * 100 : 0;
                sb.AppendLine($"   Token: {ts.TotalTokens:N0} | 命中: {hitRate:F1}% | ${ts.TotalCost:F4}");
            }
        }
        return Ok(sb.ToString().Trim());
    }

    private async Task<ToolExecutionResult> HandleRunningAsync(string sessionId)
    {
        var agents = await _mgr.GetSubAgentsAsync(sessionId);
        var running = agents.Where(s => s.Status == "running").ToList();
        if (running.Count == 0)
            return Ok("当前没有正在运行的子代理。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔄 正在运行的子代理（共 {running.Count} 个）:\n");
        foreach (var sa in running)
        {
            var elapsed = DateTimeOffset.UtcNow - sa.SpawnedAt;
            sb.AppendLine($"- `{sa.SubSessionId[^16..]}` ({sa.TemplateId ?? "默认"})");
            sb.AppendLine($"   任务: {sa.TaskSummary}");
            sb.AppendLine($"   运行时长: {elapsed.TotalSeconds:F0}秒");
            sb.AppendLine();
        }
        return Ok(sb.ToString().Trim());
    }

    private static ToolExecutionResult Ok(string output) =>
        new() { Success = true, Output = output };

    private static ToolExecutionResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed record QuerySubAgentsArgs
{
    [ToolParam("操作类型：list / stats / status / grep / recent / running")]
    public string? Action { get; init; }
    [ToolParam("子代理 ID（status 操作需要）")]
    public string? SubAgentId { get; init; }
    [ToolParam("搜索关键词（grep 操作需要）")]
    public string? Keyword { get; init; }
    [ToolParam("天数（recent 操作需要，如 1 或 7）")]
    public int? Days { get; init; }
}
