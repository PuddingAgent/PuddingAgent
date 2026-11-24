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
    description: "向 Agent 私有的 goal.md 追加带时间戳的记录，或通过 base64 编码内容整体覆盖。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class GoalUpdateTool : PuddingToolBase<GoalUpdateArgs>
{
    /// <summary>
    /// 严格 UTF-8：遇到无效字节抛 <see cref="DecoderFallbackException"/>，而不是静默替换成 U+FFFD。
    /// 静默替换会把上游的编码损坏包装成「写入成功」，是 2026-11-25 goal.md 被写坏 1747 处替换字符的直接放大器。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 写入用 UTF-8：不带 BOM。goal.md 约定禁 BOM，而 <see cref="Encoding.UTF8"/> 静态实例会写 BOM。
    /// </summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

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
                    var raw = Convert.FromBase64String(args.ContentBase64!);
                    content = StrictUtf8.GetString(raw);
                    mode = "override";
                }
                catch (FormatException)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "content_base64 解码失败：不是有效的 Base64 字符串。"));
                }
                catch (DecoderFallbackException)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "content_base64 是合法 Base64，但解出的字节流不是有效 UTF-8（疑似传入了压缩流或二进制）。"
                        + "已拒绝写入，以免用替换字符污染 goal.md。"));
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

            // 守卫：U+FFFD 替换字符是编码损坏的痕迹。写进 goal.md 只会把损坏变成「看起来正常的坏文件」，
            // 而调用方仍收到成功回执。2026-11-25 事故即此形态，故此处 fail-closed。
            var fffdCount = 0;
            var fffdFirstIndex = -1;
            for (var i = 0; i < content!.Length; i++)
            {
                if (content[i] != '\uFFFD') continue;
                fffdCount++;
                if (fffdFirstIndex < 0) fffdFirstIndex = i;
            }

            if (fffdCount > 0)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"内容含 {fffdCount} 处 U+FFFD 替换字符（首个位于第 {fffdFirstIndex} 个字符），"
                    + "通常是上游编码损坏的痕迹。已拒绝写入以免污染 goal.md，请重新生成内容。"));
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

            // 原子写 + 写后自检：先落 .tmp、按字节读回比对、一致才 Move。
            // 「写盘工具的回显不是验收证据」——下面日志里的 size 是字符数，与磁盘字节数无关。
            var tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            var expectedBytes = NoBomUtf8.GetBytes(newContent);
            File.WriteAllBytes(tmpPath, expectedBytes);

            var readBackBytes = File.ReadAllBytes(tmpPath);
            if (!readBackBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                try { File.Delete(tmpPath); } catch { /* 清理失败不掩盖主错误 */ }
                throw new InvalidOperationException(
                    $"写入自检失败：期望 {expectedBytes.Length} 字节，回读 {readBackBytes.Length} 字节，已放弃写入。");
            }

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

            // 写入后检查文件大小是否超过告警阈值（32 KB）
            var newSize = newContent.Length;
            string? sizeWarning = null;
            if (newSize > GoalReadTool.WarnLimitBytes)
            {
                var sizeKB = newSize / 1024.0;
                sizeWarning = $"goal.md 已达 {sizeKB:F1} KB（建议上限 {GoalReadTool.WarnLimitBytes / 1024} KB），请尽快用 content_base64 覆盖整理，将历史归档到 memory/ 或记忆库。";
                _logger.LogWarning("[GoalUpdate] goal.md size warning path={Path} size={Size} limit={Limit}",
                    pathLabel, newSize, GoalReadTool.WarnLimitBytes);
            }

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
                    new_size = newSize,
                    bytes_changed = Math.Abs(newSize - oldSize),
                    old_lines = oldLineCount,
                    new_lines = newLineCount,
                    lines_changed = newLineCount - oldLineCount,
                },
                timestamp,
                size_warning = sizeWarning,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GoalUpdate] Failed to update goal.md");
            // 必须让调用方看到失败：此前这里返回 {"status":"error"} 但外层仍包装成 Success=true，
            // 等于写失败也回「成功」，与本次事故同一类静默。
            throw;
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
