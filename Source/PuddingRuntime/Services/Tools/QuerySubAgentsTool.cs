using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// QuerySubAgentsTool — Agent 自我查询子代理状态的工具。
/// 
/// 提供以下操作：
///   · list — 列出当前会话的所有子代理（含状态、任务、时间）
///   · stats — 获取子代理统计摘要
///   · status {sub_agent_id} — 查询指定子代理的详细状态
///   · grep {keyword} — 搜索子代理（任务描述/结果摘要）
///   · recent {days} — 最近 N 天的子代理
///   · running — 仅列出正在运行的子代理
/// 
/// 实现 ITool（LLM function calling）和 IAgentSkill（SkillRuntime）双接口。
/// </summary>
public sealed class QuerySubAgentsTool : IAgentSkill
{
    private readonly ISubAgentManager _mgr;
    private readonly ILogger<QuerySubAgentsTool> _logger;

    public string SkillId => "query_sub_agents";
    public string Name => "query_sub_agents";
    public string Description =>
        "查询当前会话的子代理状态。支持操作：list（列出全部）、stats（统计摘要）、" +
        "status {id}（查询指定子代理）、grep {keyword}（搜索）、recent {days}（时间范围）、" +
        "running（仅运行中）。";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "操作类型：list / stats / status / grep / recent / running"),
            new("sub_agent_id", "string", "子代理 ID（status 操作需要）"),
            new("keyword", "string", "搜索关键词（grep 操作需要）"),
            new("days", "integer", "天数（recent 操作需要，如 1 或 7）"),
        ],
        ["action"]);

    public QuerySubAgentsTool(
        ISubAgentManager mgr,
        ILogger<QuerySubAgentsTool> logger)
    {
        _mgr = mgr;
        _logger = logger;
    }

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var json = TryParseJson(request.Input);
        var action = GetStringProp(json, "action")
                     ?? request.Parameters.GetValueOrDefault("action")
                     ?? "list";
        var sessionId = request.SessionId;

        _logger.LogDebug("[QuerySubAgents] action={Action} session={Session}", action, sessionId);

        try
        {
            return action.ToLowerInvariant() switch
            {
                "list" => await HandleListAsync(sessionId),
                "stats" => await HandleStatsAsync(sessionId),
                "status" => await HandleStatusAsync(json, request, sessionId),
                "grep" => await HandleGrepAsync(json, request, sessionId),
                "recent" => await HandleRecentAsync(json, request, sessionId),
                "running" => await HandleRunningAsync(sessionId),
                _ => Fail($"未知操作 '{action}'。支持：list / stats / status / grep / recent / running")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QuerySubAgents] Failed action={Action} session={Session}", action, sessionId);
            return Fail($"查询子代理失败: {ex.Message}");
        }
    }

    private async Task<SkillResult> HandleListAsync(string sessionId)
    {
        var agents = await _mgr.GetSubAgentsAsync(sessionId);
        if (agents.Count == 0)
            return new SkillResult { Success = true, Output = "当前会话没有任何子代理记录。", ExitCode = 0 };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📊 子代理列表（共 {agents.Count} 个）:\n");
        foreach (var sa in agents)
        {
            var icon = sa.Status switch
            {
                "running" => "🔄",
                "completed" => "✅",
                "failed" => "❌",
                _ => "❓"
            };
            var shortId = sa.SubSessionId.Length > 20 ? "..." + sa.SubSessionId[^16..] : sa.SubSessionId;
            sb.AppendLine($"{icon} `{shortId}` [{sa.Status}]");
            sb.AppendLine($"   模板: {sa.TemplateId ?? "默认"} | 模型: {sa.ModelId ?? "默认"}");
            sb.AppendLine($"   任务: {sa.TaskSummary}");
            sb.AppendLine($"   创建: {sa.SpawnedAt:HH:mm:ss} | 完成: {sa.CompletedAt?.ToString("HH:mm:ss") ?? "-"}");
            if (sa.ResultSummary is not null)
                sb.AppendLine($"   结果: {sa.ResultSummary}");
            sb.AppendLine();
        }
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private async Task<SkillResult> HandleStatsAsync(string sessionId)
    {
        var stats = await _mgr.GetStatsAsync(sessionId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📈 子代理统计:");
        sb.AppendLine($"   总计: {stats.Total}");
        sb.AppendLine($"   运行中: {stats.Running}");
        sb.AppendLine($"   已完成: {stats.Completed}");
        sb.AppendLine($"   已失败: {stats.Failed}");
        if (stats.LastCompletedId is not null)
            sb.AppendLine($"   最近完成: {stats.LastCompletedId[^16..]}");
        if (stats.LastFailedId is not null)
            sb.AppendLine($"   最近失败: {stats.LastFailedId[^16..]}");
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private async Task<SkillResult> HandleStatusAsync(JsonNode? json, SkillInvokeRequest request, string sessionId)
    {
        var subId = GetStringProp(json, "sub_agent_id")
                    ?? request.Parameters.GetValueOrDefault("sub_agent_id");
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
        if (status.ResultSummary is not null)
            sb.AppendLine($"   结果: {status.ResultSummary}");
        if (status.Success.HasValue)
            sb.AppendLine($"   成功: {status.Success.Value}");
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private async Task<SkillResult> HandleGrepAsync(JsonNode? json, SkillInvokeRequest request, string sessionId)
    {
        var keyword = GetStringProp(json, "keyword")
                      ?? request.Parameters.GetValueOrDefault("keyword");
        if (string.IsNullOrWhiteSpace(keyword))
            return Fail("grep 操作需要 keyword 参数");

        var results = await _mgr.GrepAsync(sessionId, keyword);
        if (results.Count == 0)
            return new SkillResult { Success = true, Output = $"未找到匹配 '{keyword}' 的子代理。", ExitCode = 0 };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔍 搜索 '{keyword}' — 找到 {results.Count} 个结果:\n");
        foreach (var sa in results)
        {
            sb.AppendLine($"- {sa.SubSessionId[^16..]} [{sa.Status}] {sa.TaskSummary[..Math.Min(sa.TaskSummary.Length, 60)]}");
        }
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private async Task<SkillResult> HandleRecentAsync(JsonNode? json, SkillInvokeRequest request, string sessionId)
    {
        var daysStr = GetStringProp(json, "days")
                      ?? request.Parameters.GetValueOrDefault("days")
                      ?? "7";
        if (!int.TryParse(daysStr, out var days) || days < 1)
            days = 7;

        var results = await _mgr.GetRecentAsync(sessionId, days);
        if (results.Count == 0)
            return new SkillResult { Success = true, Output = $"最近 {days} 天没有子代理记录。", ExitCode = 0 };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📅 最近 {days} 天的子代理（共 {results.Count} 个）:\n");
        foreach (var sa in results)
        {
            var icon = sa.Status switch { "running" => "🔄", "completed" => "✅", "failed" => "❌", _ => "❓" };
            sb.AppendLine($"{icon} {sa.SpawnedAt:MM-dd HH:mm} [{sa.Status}] {sa.TaskSummary[..Math.Min(sa.TaskSummary.Length, 50)]}");
        }
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private async Task<SkillResult> HandleRunningAsync(string sessionId)
    {
        var agents = await _mgr.GetSubAgentsAsync(sessionId);
        var running = agents.Where(s => s.Status == "running").ToList();
        if (running.Count == 0)
            return new SkillResult { Success = true, Output = "当前没有正在运行的子代理。", ExitCode = 0 };

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
        return new SkillResult { Success = true, Output = sb.ToString().Trim(), ExitCode = 0 };
    }

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = "", Error = error, ExitCode = 1 };

    private static string? GetStringProp(JsonNode? json, string name) =>
        json?[name]?.GetValue<string>();

    private static JsonNode? TryParseJson(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try { return JsonNode.Parse(input); }
        catch { return null; }
    }
}
