using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 更新调用方 Agent 私有的 goal.md 文件。
/// 
/// 设计哲学：goal.md 是 Agent 给自己的便条，不是结构化 API。
/// 工具只做写入——Agent 全权负责内容组织和 Markdown 结构。
///
/// 两种模式：
///   - append：追加带时间戳的条目到文件末尾（日常笔记/决策日志）
///   - content_base64：Base64 编码的完整 goal.md 内容，完全覆盖文件（重新组织时使用）
///
/// 文件路径：{DataRoot}/agents/{agentInstanceId}/goal.md
/// 回退路径：{DataRoot}/workspaces/{workspaceId}/goal.md
/// </summary>
[Tool(
    id: "goal_update",
    name: "Goal update",
    description: "Append a timestamped entry to the agent's private goal.md, or overwrite it entirely via base64-encoded content.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class GoalUpdateTool : PuddingToolBase<GoalUpdateArgs>
{
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<GoalUpdateTool> _logger;

    public GoalUpdateTool(PuddingDataPaths paths, ILogger<GoalUpdateTool> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalUpdateArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var agentInstanceId = args.AgentInstanceId ?? context.AgentInstanceId ?? "";
            var workspaceId = args.WorkspaceId ?? context.WorkspaceId ?? "default";

            string? content = null;
            string mode;

            if (!string.IsNullOrWhiteSpace(args.ContentBase64))
            {
                try
                {
                    content = Encoding.UTF8.GetString(Convert.FromBase64String(args.ContentBase64!));
                    mode = "override";
                }
                catch (FormatException)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "content_base64 解码失败：不是有效的 Base64 字符串。"));
                }
            }
            else if (!string.IsNullOrWhiteSpace(args.Append))
            {
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                content = $"\n---\n\n**{timestamp}**\n\n{args.Append}";
                mode = "append";
            }
            else
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    "至少需要 append 或 content_base64 之一。"
                    + "append=\"内容\" 追加到文件末尾；content_base64=\"...\" 完全覆盖文件。"));
            }

            var result = WriteGoalFile(mode, content!, agentInstanceId, workspaceId);
            return Task.FromResult(ToolExecutionResult.Ok(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
        }
    }

    private string WriteGoalFile(string mode, string content, string agentInstanceId, string workspaceId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Determine target file path
        string filePath;
        bool isAgentPath;

        if (!string.IsNullOrWhiteSpace(agentInstanceId))
        {
            var dir = _paths.AgentInstanceRoot(agentInstanceId);
            Directory.CreateDirectory(dir);
            filePath = Path.Combine(dir, "goal.md");
            isAgentPath = true;
        }
        else
        {
            var dir = Path.Combine(_paths.WorkspacesRoot, workspaceId);
            Directory.CreateDirectory(dir);
            filePath = Path.Combine(dir, "goal.md");
            isAgentPath = false;
        }

        try
        {
            var existing = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
            string newContent;

            if (mode == "append")
            {
                newContent = existing.TrimEnd() + content + "\n";
            }
            else // override
            {
                newContent = content;
            }

            // Atomic write: .tmp then Move
            var tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(tmpPath, newContent, Encoding.UTF8);
            File.Move(tmpPath, filePath, overwrite: true);

            var pathLabel = isAgentPath
                ? $"agents/{agentInstanceId}/goal.md"
                : $"workspaces/{workspaceId}/goal.md";

            _logger.LogInformation(
                "[GoalUpdate] {Mode} {Path} size={Size}",
                mode, pathLabel, newContent.Length);

            var oldSize = string.IsNullOrEmpty(existing) ? 0 : existing.Length;
            var oldLineCount = string.IsNullOrEmpty(existing) ? 0 : existing.Split('\n').Length;
            var newLineCount = newContent.Split('\n').Length;

            return JsonSerializer.Serialize(new
            {
                status = "ok",
                mode,
                path = pathLabel,
                agent_instance_id = isAgentPath ? agentInstanceId : null,
                workspace_id = isAgentPath ? null : workspaceId,
                change = new
                {
                    old_size = oldSize,
                    new_size = newContent.Length,
                    bytes_changed = Math.Abs(newContent.Length - oldSize),
                    old_lines = oldLineCount,
                    new_lines = newLineCount,
                    lines_changed = newLineCount - oldLineCount,
                },
                timestamp,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GoalUpdate] Failed to update goal.md");
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message,
            });
        }
    }
}

public sealed record GoalUpdateArgs
{
    [ToolParam("追加到 goal.md 末尾的内容（自动添加时间戳）。用于日常笔记、决策记录。")]
    public string? Append { get; init; }

    [ToolParam("Base64 编码的完整 goal.md 内容。用于完全重写文件（重新组织时使用）。")]
    public string? ContentBase64 { get; init; }

    [ToolParam("Agent 实例 ID（可选，默认当前 Agent 实例）")]
    public string? AgentInstanceId { get; init; }

    [ToolParam("工作区 ID（可选，默认 'default'）")]
    public string? WorkspaceId { get; init; }
}
