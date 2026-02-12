using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// 会话归档器：将完整会话的原始记录导出为纯文本 Markdown 文件。
/// 存储路径：data/logs/sessions/{date}/{sessionId}.md
/// 每个会话一个独立文件，按日期分目录。
/// </summary>
public class SessionArchiver
{
    private readonly ILogger<SessionArchiver> _logger;
    private readonly string _baseDir;

    public SessionArchiver(ILogger<SessionArchiver> logger)
    {
        _logger = logger;
        _baseDir = Path.Combine(AppContext.BaseDirectory, "data", "logs", "sessions");
        Directory.CreateDirectory(_baseDir);
    }

    /// <summary>
    /// 将会话消息列表导出为 Markdown 纯文本文件。
    /// </summary>
    public async Task ArchiveAsync(
        string sessionId,
        string workspaceId,
        string agentName,
        IReadOnlyList<(string Role, string Content, long Timestamp)> messages,
        CancellationToken ct = default)
    {
        if (messages.Count == 0) return;

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var dir = Path.Combine(_baseDir, date);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, $"{sessionId}.md");
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# Session: {sessionId}");
        sb.AppendLine($"- Workspace: {workspaceId}");
        sb.AppendLine($"- Agent: {agentName}");
        sb.AppendLine($"- Messages: {messages.Count}");
        sb.AppendLine($"- Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var (role, content, ts) in messages)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToString("yyyy-MM-dd HH:mm:ss");
            sb.AppendLine($"## {role}");
            sb.AppendLine($"> {time}");
            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), ct);
        _logger.LogInformation("[SessionArchiver] Archived session={SessionId} to {Path}", sessionId, filePath);
    }
}
