using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent 自适应心跳控制工具。Agent 在每次心跳工作完成后调用此工具，
/// 告诉系统：在空闲持续至少一段合理时间后再次唤醒我。
///
/// 不是精确闹钟 — Pudding 的「尽力模式」意味着：
/// - 如果中间有用户消息进来，心跳会被消息处理覆盖
/// - 如果 Agent 正忙，心跳不会强行打断
/// - 如果队列前面有其他 Agent，会依次排队
///
/// 安全护栏（Pudding 强制截断）：
///   min_idle_seconds: 60~86400 (默认 60)
///   max_idle_seconds: min~86400 (默认 min)
///   min ≤ max 强制验证
/// </summary>
[Tool(
    id: "sleep",
    name: "Agent sleep",
    description: "Set the agent's heartbeat interval and wake cycle. The agent controls its own heartbeat frequency.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class AgentSleepTool : PuddingToolBase<AgentSleepArgs>
{
    private readonly AgentWakeQueue _wakeQueue;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<AgentSleepTool> _logger;

    public AgentSleepTool(
        AgentWakeQueue wakeQueue,
        PuddingDataPaths paths,
        ILogger<AgentSleepTool> logger)
    {
        _wakeQueue = wakeQueue;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentSleepArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var agentId = context.AgentInstanceId;
            var result = await ExecuteCoreInternalAsync(agentId, args, ct);
            return ToolExecutionResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<string> ExecuteCoreInternalAsync(
        string agentId, AgentSleepArgs args, CancellationToken ct)
    {
        // ── Pudding 强制护栏：clamp 到安全区间 ──
        var minSeconds = Math.Clamp(args.MinIdleSeconds ?? 60, 60, 86400);
        var maxSeconds = Math.Clamp(args.MaxIdleSeconds ?? minSeconds, minSeconds, 86400);

        if (minSeconds > maxSeconds)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = "min_idle_seconds 不能大于 max_idle_seconds",
            });
        }

        var minIdle = TimeSpan.FromSeconds(minSeconds);
        var maxIdle = TimeSpan.FromSeconds(maxSeconds);

        await _wakeQueue.EnqueueAsync(agentId, minIdle, maxIdle, ct);

        // 持久化心跳偏好到磁盘
        await PersistHeartbeatAsync(agentId, minSeconds, maxSeconds, args.WorkSummary);

        if (!string.IsNullOrWhiteSpace(args.WorkSummary))
        {
            _logger.LogInformation("[AgentSleep] agent={Agent} summary={Summary}", agentId, args.WorkSummary);
        }

        _logger.LogInformation(
            "[AgentSleep] agent={Agent} min={Min}s max={Max}s",
            agentId, minSeconds, maxSeconds);

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            message = $"已登记：期望空闲 {minSeconds}～{maxSeconds} 秒后下次心跳（尽力模式，不保证精确时间）。",
            min_idle_seconds = minSeconds,
            max_idle_seconds = maxSeconds,
        });
    }

    /// <summary>
    /// 将心跳偏好写入 {AgentInstanceRoot(agentId)}/heartbeat.json。
    /// 写入失败不影响主流程（仅记录日志）。
    /// </summary>
    private async Task PersistHeartbeatAsync(string agentId, int minIdle, int maxIdle, string? summary)
    {
        try
        {
            var agentDir = _paths.AgentInstanceRoot(agentId);
            if (!Directory.Exists(agentDir))
                Directory.CreateDirectory(agentDir);

            var filePath = Path.Combine(agentDir, "heartbeat.json");
            var pref = new HeartbeatPreference
            {
                AgentId = agentId,
                MinIdleSeconds = minIdle,
                MaxIdleSeconds = maxIdle,
                WorkSummary = summary ?? "",
                UpdatedAt = DateTime.UtcNow,
            };

            var json = JsonSerializer.Serialize(pref,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);

            _logger.LogDebug("[AgentSleep] Persisted heartbeat to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentSleep] Failed to persist heartbeat for agent={Agent}", agentId);
        }
    }
}

public sealed record AgentSleepArgs
{
    [ToolParam("期望至少空闲多少秒后才考虑下次唤醒（60~86400）")]
    public int? MinIdleSeconds { get; init; }

    [ToolParam("期望最迟在空闲多少秒后必须唤醒（min~86400）")]
    public int? MaxIdleSeconds { get; init; }

    [ToolParam("本次心跳的工作摘要，会记录到日志中")]
    public string? WorkSummary { get; init; }
}
