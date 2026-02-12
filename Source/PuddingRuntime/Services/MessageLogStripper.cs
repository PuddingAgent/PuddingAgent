using System.Text;

namespace PuddingRuntime.Services;

/// <summary>
/// 从原始消息日志 .md 文件中剥离非对话帧（event/thinking/tool_call/tool_result），
/// 仅保留 [User] 和 [Assistant] 行，用于冷启动时 L5-RECENT 层预填充。
/// </summary>
internal static class MessageLogStripper
{
    /// <summary>剥离后每行的最大字符数（防止单行过长）。</summary>
    private const int MaxLineChars = 500;

    /// <summary>
    /// 剥离原始日志，仅保留用户和助手对话行。
    /// event / thinking / tool_call / tool_result / delta 及对应内容块全部丢弃。
    /// </summary>
    public static string Strip(string rawLog)
    {
        var lines = rawLog.Split('\n');
        var sb = new StringBuilder(rawLog.Length / 2);
        var inSkippedBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // 跳过 event/thinking/tool_call/tool_result 块起始
            if (trimmed.StartsWith("[Event]") ||
                trimmed.StartsWith("[Thinking]") ||
                trimmed.StartsWith("[Tool Call]") ||
                trimmed.StartsWith("[Tool Result]") ||
                trimmed.StartsWith("[delta]"))
            {
                inSkippedBlock = true;
                continue;
            }

            // 遇到下一个消息类型 → 结束跳过
            if (trimmed.StartsWith("[User]") || trimmed.StartsWith("[Assistant]"))
            {
                inSkippedBlock = false;
                var stripped = trimmed.Length > MaxLineChars
                    ? trimmed[..MaxLineChars] + "..."
                    : trimmed;
                sb.AppendLine(stripped);
                continue;
            }

            // 跳过 <details> / </details> 折叠块
            if (trimmed.StartsWith("<details>") || trimmed.StartsWith("</details>"))
            {
                inSkippedBlock = true;
                continue;
            }

            // 跳过 ``` 代码块
            if (trimmed.StartsWith("```"))
            {
                inSkippedBlock = !inSkippedBlock;
                continue;
            }

            // 在跳过块中 → 丢弃
            if (inSkippedBlock) continue;

            // 空行保留（对话间距）
            if (string.IsNullOrEmpty(trimmed))
            {
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }
}
