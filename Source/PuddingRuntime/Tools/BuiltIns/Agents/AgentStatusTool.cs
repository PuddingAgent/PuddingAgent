using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPlatform.Services;
using PuddingRuntime.Models;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 查看工作区内 Agent 的运行状态：心跳频率、目标状态、队列状态、最近活动时间等。
/// 支持查看全部 Agent 或指定单个 Agent。
/// </summary>
[Tool(
    id: "agent_status",
    name: "Agent status",
    description: "View runtime status of agents in the workspace: heartbeat frequency, goal status, queue status, recent activity, and more.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class AgentStatusTool : PuddingToolBase<AgentStatusArgs>
{
    private const int DefaultHeartbeatSeconds = 3600;

    private readonly IWorkspaceAgentCatalog _catalog;
    private readonly PuddingDataPaths _paths;
    private readonly AgentWakeQueue _wakeQueue;
    private readonly IIdleDetector _idleDetector;
    private readonly ILogger<AgentStatusTool> _logger;

    public AgentStatusTool(
        IWorkspaceAgentCatalog catalog,
        PuddingDataPaths paths,
        AgentWakeQueue wakeQueue,
        IIdleDetector idleDetector,
        ILogger<AgentStatusTool> logger)
    {
        _catalog = catalog;
        _paths = paths;
        _wakeQueue = wakeQueue;
        _idleDetector = idleDetector;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentStatusArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var result = await ExecuteCoreInternalAsync(args.AgentId, ct);
            return ToolExecutionResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<string> ExecuteCoreInternalAsync(string? agentId, CancellationToken ct)
    {
        var allAgents = await _catalog.ListAgentsAsync("default", ct);
        if (allAgents.Count == 0)
            return JsonSerializer.Serialize(new { status = "empty", message = "工作区中没有 Agent。" });

        var agents = !string.IsNullOrWhiteSpace(agentId)
            ? allAgents.Where(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase)).ToList()
            : allAgents;

        if (agents.Count == 0)
            return JsonSerializer.Serialize(new { status = "not_found", message = $"未找到 Agent: {agentId}" });

        var globalIdle = _idleDetector.IdleDuration;
        var reports = new List<object>();

        foreach (var agent in agents)
        {
            var heartbeat = await ReadHeartbeatAsync(agent.AgentId, ct);
            var goalInfo = CheckGoal(agent.AgentId);
            var inQueue = await _wakeQueue.IsInQueueAsync(agent.AgentId, ct);
            var wake = inQueue ? await _wakeQueue.GetWakeRequestAsync(agent.AgentId, ct) : null;
            var lastActivityMinutes = GetLastActivityMinutes(agent.AgentId);

            var status = inQueue ? "sleeping" : "idle";

            reports.Add(new
            {
                agent_id = agent.AgentId,
                name = agent.DisplayName ?? agent.Name,
                role = ResolveRole(agent.SourceTemplateId),
                status,
                heartbeat = new
                {
                    active = heartbeat is not null,
                    min_idle_seconds = heartbeat?.MinIdleSeconds ?? DefaultHeartbeatSeconds,
                    max_idle_seconds = heartbeat?.MaxIdleSeconds ?? DefaultHeartbeatSeconds,
                },
                goal = new
                {
                    has_goal = goalInfo.hasGoal,
                    summary = goalInfo.summary,
                },
                last_activity_minutes_ago = lastActivityMinutes,
                in_queue = inQueue,
                estimated_wake_seconds = wake is not null
                    ? (int)(wake.EarliestWakeAt - DateTime.UtcNow).TotalSeconds
                    : (int?)null,
                global_idle_seconds = (int)globalIdle.TotalSeconds,
            });
        }

        var text = FormatTextReport(reports);
        var json = JsonSerializer.Serialize(reports.Count == 1 ? reports[0] : reports,
            new JsonSerializerOptions { WriteIndented = true });

        return $"{text}\n\n---\n{json}";
    }

    private async Task<HeartbeatPreference?> ReadHeartbeatAsync(string agentId, CancellationToken ct)
    {
        try
        {
            var filePath = Path.Combine(_paths.AgentInstanceRoot(agentId), "heartbeat.json");
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<HeartbeatPreference>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentStatus] Failed to read heartbeat for agent={Agent}", agentId);
            return null;
        }
    }

    private (bool hasGoal, string? summary) CheckGoal(string agentId)
    {
        try
        {
            var goalPath = Path.Combine(_paths.AgentInstanceRoot(agentId), "goal.md");
            if (!File.Exists(goalPath)) return (false, null);

            var content = File.ReadAllText(goalPath);
            var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim('#', ' ', '\r');
            return (content.Trim().Length > 0, firstLine);
        }
        catch
        {
            return (false, null);
        }
    }

    private double? GetLastActivityMinutes(string agentId)
    {
        try
        {
            var logsRoot = _paths.AgentInstanceMessageLogsRoot(agentId);
            if (!Directory.Exists(logsRoot)) return null;

            var dayDirs = Directory.GetDirectories(logsRoot);
            if (dayDirs.Length == 0) return null;

            var latestDay = dayDirs
                .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                .OrderByDescending(d => d.Name)
                .First();

            var files = Directory.GetFiles(latestDay.Path, "*.jsonl");
            if (files.Length == 0)
            {
                files = Directory.GetFiles(latestDay.Path, "*.md");
                if (files.Length == 0) return null;
            }

            var latestFile = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var lastWrite = File.GetLastWriteTimeUtc(latestFile);
            return (DateTime.UtcNow - lastWrite).TotalMinutes;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTextReport(List<object> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Agent Status Report");
        sb.AppendLine(new string('═', 55));

        foreach (dynamic r in reports)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.name} ({r.role})");
            sb.AppendLine($"  Agent ID:  {r.agent_id}");
            sb.AppendLine($"  Status:    {StatusLabel((string)r.status)}");
            sb.AppendLine($"  Heartbeat: {(r.heartbeat.active ? $"active (min={r.heartbeat.min_idle_seconds}s, max={r.heartbeat.max_idle_seconds}s)" : "inactive (无心跳配置)")}");
            sb.AppendLine($"  Goal:      {(r.goal.has_goal ? r.goal.summary + " (活跃)" : "无")}");

            if (r.last_activity_minutes_ago is double mins)
                sb.AppendLine($"  Last Activity: {FormatMinutes(mins)}");
            else
                sb.AppendLine($"  Last Activity: 未知");

            if (r.in_queue)
            {
                var wakeLabel = r.estimated_wake_seconds is int secs && secs > 0
                    ? $"预计 {secs}s 后唤醒"
                    : "等待中";
                sb.AppendLine($"  In Queue:  是 ({wakeLabel})");
            }
            else
            {
                sb.AppendLine($"  In Queue:  否");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string StatusLabel(string status) => status switch
    {
        "sleeping" => "休眠中",
        "idle" => "空闲中",
        _ => status,
    };

    private static string FormatMinutes(double minutes)
    {
        if (minutes < 1) return "不到 1 分钟前";
        if (minutes < 60) return $"{(int)minutes} 分钟前";
        var hours = minutes / 60;
        if (hours < 24) return $"{(int)hours} 小时前";
        return $"{(int)(hours / 24)} 天前";
    }

    private static string ResolveRole(string? templateId) => templateId switch
    {
        "general-assistant" => "通用助手",
        "code-assistant" => "代码助手",
        "research-assistant" => "研究助手",
        "workspace-audit-assistant" => "审计助手",
        _ => templateId ?? "未知",
    };
}

public sealed record AgentStatusArgs
{
    [ToolParam("Optional agent instance id to query. If omitted, lists all agents.")]
    public string? AgentId { get; init; }
}
