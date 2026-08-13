using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 读取调用方 Agent 私有的 goal.md 文件内容。
/// goal.md 是 Agent 主动心跳系统的目标载体，驱动每次心跳的思考方向。
/// 
/// 文件路径：{DataRoot}/agents/{agentInstanceId}/goal.md
/// 回退路径：{DataRoot}/workspaces/{workspaceId}/goal.md（当 agent_instance_id 未提供时）
/// </summary>
[Tool(
    id: "goal_read",
    name: "Goal read",
    description: "读取当前 Agent 私有的 goal.md 文件内容。Read the current agent's private goal.md file content",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class GoalReadTool : PuddingToolBase<GoalReadArgs>
{
    /// <summary>goal.md 读取上限字节数（16 KB）。超过此上限只返回尾部内容。</summary>
    public const int ReadLimitBytes = 16 * 1024;

    /// <summary>goal.md 写入后告警阈值字节数（32 KB）。</summary>
    public const int WarnLimitBytes = 32 * 1024;

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<GoalReadTool> _logger;

    public GoalReadTool(PuddingDataPaths paths, ILogger<GoalReadTool> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalReadArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var agentInstanceId = context.AgentInstanceId ?? "";
            var workspaceId = context.WorkspaceId ?? "default";
            var result = ReadGoalFile(agentInstanceId, workspaceId);
            return Task.FromResult(ToolExecutionResult.Ok(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
        }
    }

    private string ReadGoalFile(string agentInstanceId, string workspaceId)
    {
        // 优先使用 Agent 私有路径
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
        {
            var agentGoalPath = Path.Combine(_paths.AgentInstanceRoot(agentInstanceId), "goal.md");
            if (File.Exists(agentGoalPath))
            {
                try
                {
                    var (content, truncated) = ReadWithSizeLimit(agentGoalPath);
                    _logger.LogDebug("[GoalRead] Read agent goal.md agent={Agent} size={Size} truncated={Truncated}",
                        agentInstanceId, content.Length, truncated);
                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        agent_instance_id = agentInstanceId,
                        path = "agents/" + agentInstanceId + "/goal.md",
                        content,
                        truncated,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GoalRead] Failed to read agent goal.md agent={Agent}", agentInstanceId);
                    return JsonSerializer.Serialize(new
                    {
                        status = "error",
                        message = ex.Message,
                        agent_instance_id = agentInstanceId,
                    });
                }
            }
        }

        // 回退：workspace 路径
        var wsGoalPath = Path.Combine(_paths.WorkspacesRoot, workspaceId, "goal.md");
        if (!File.Exists(wsGoalPath))
        {
            return JsonSerializer.Serialize(new
            {
                status = "not_found",
                message = "当前没有设置目标，请先设置一个目标。如果不需要目标，可以忽略此提醒。\n" +
                    $"目标文件路径: {_paths.AgentInstanceRoot(agentInstanceId)}\\goal.md",
                agent_instance_id = agentInstanceId,
                workspace_id = workspaceId,
            });
        }

        try
        {
            var (content, truncated) = ReadWithSizeLimit(wsGoalPath);
            _logger.LogDebug("[GoalRead] Read workspace goal.md workspace={Workspace} size={Size} truncated={Truncated}",
                workspaceId, content.Length, truncated);
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                workspace_id = workspaceId,
                path = "workspaces/" + workspaceId + "/goal.md",
                content,
                truncated,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GoalRead] Failed to read goal.md workspace={Workspace}", workspaceId);
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message,
                workspace_id = workspaceId,
            });
        }
    }

    /// <summary>
    /// 读取文件，若超过 ReadLimitBytes 则只返回尾部内容并在前方附加告警头。
    /// 截断点尽量落在换行边界，不破坏 UTF-8 多字节序列。
    /// </summary>
    internal static (string Content, bool Truncated) ReadWithSizeLimit(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var totalBytes = fileInfo.Length;

        if (totalBytes <= ReadLimitBytes)
        {
            return (File.ReadAllText(filePath, Encoding.UTF8), false);
        }

        // 读取尾部 ReadLimitBytes 字节
        var buffer = new byte[ReadLimitBytes];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(Math.Max(0, totalBytes - ReadLimitBytes), SeekOrigin.Begin);
        var bytesRead = fs.Read(buffer, 0, ReadLimitBytes);

        // 跳过起始位置可能的残缺 UTF-8 字节（多字节字符的续字节以 10xxxxxx 开头）
        var start = 0;
        while (start < bytesRead && (buffer[start] & 0xC0) == 0x80)
            start++;

        var tailContent = Encoding.UTF8.GetString(buffer, start, bytesRead - start);

        // 尽量在第一个换行符之后开始，保留完整行
        var newlineIdx = tailContent.IndexOf('\n');
        if (newlineIdx >= 0 && newlineIdx < tailContent.Length - 1)
            tailContent = tailContent[(newlineIdx + 1)..];

        var totalKB = totalBytes / 1024.0;
        var warning = $"[goal.md 共 {totalKB:F1} KB，超过读取上限 {ReadLimitBytes / 1024.0:F0} KB，以下为文件最近内容。建议用 goal_update 的 content_base64 覆盖模式整理或归档历史内容到 memory/ 或记忆库。]\n\n";

        return (warning + tailContent, true);
    }
}

public sealed record GoalReadArgs
{
    // goal_read 无参数——所有上下文从 ToolExecutionContext 获取
}
