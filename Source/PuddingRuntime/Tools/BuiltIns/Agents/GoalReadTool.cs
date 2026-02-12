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
    description: "Read the current agent's private goal.md file content.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class GoalReadTool : PuddingToolBase<GoalReadArgs>
{
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
                    var content = File.ReadAllText(agentGoalPath);
                    _logger.LogDebug("[GoalRead] Read agent goal.md agent={Agent} size={Size}",
                        agentInstanceId, content.Length);
                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        agent_instance_id = agentInstanceId,
                        path = "agents/" + agentInstanceId + "/goal.md",
                        content,
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
            var content = File.ReadAllText(wsGoalPath);
            _logger.LogDebug("[GoalRead] Read workspace goal.md workspace={Workspace} size={Size}",
                workspaceId, content.Length);
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                workspace_id = workspaceId,
                path = "workspaces/" + workspaceId + "/goal.md",
                content,
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
}

public sealed record GoalReadArgs
{
    // goal_read 无参数——所有上下文从 ToolExecutionContext 获取
}
