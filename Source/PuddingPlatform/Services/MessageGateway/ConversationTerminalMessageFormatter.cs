using System.Text.Json;

namespace PuddingPlatform.Services.MessageGateway;

/// <summary>
/// Converts a committed Conversation terminal payload into user-facing content.
/// The event remains the source of truth; this formatter only supplies a clear
/// presentation for channels that cannot render lifecycle facts directly.
/// </summary>
public static class ConversationTerminalMessageFormatter
{
    public const string SyntheticEmptyReply = "（Agent 未返回可展示文本）";

    public static ConversationTerminalPresentation? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var kind = ReadString(root, "kind");
            var reply = ReadString(root, "reply");
            var errorCode = ReadString(root, "errorCode");
            var errorMessage = ReadString(root, "errorMessage");

            // Kind is the lifecycle authority. Cancellation carries an error code
            // for diagnostics, but is not a failed request or a completed reply.
            if (string.Equals(kind, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                var partialReply = !string.IsNullOrWhiteSpace(reply)
                    && !string.Equals(reply.Trim(), SyntheticEmptyReply, StringComparison.Ordinal)
                        ? reply.Trim()
                        : null;
                return new ConversationTerminalPresentation(
                    partialReply is null
                        ? "请求已取消。"
                        : $"{partialReply}\n\n请求已取消，以上为停止前已生成的内容。",
                    "请求已取消",
                    IsError: false);
            }

            if (!string.IsNullOrWhiteSpace(reply)
                && !string.Equals(
                    reply.Trim(),
                    SyntheticEmptyReply,
                    StringComparison.Ordinal))
            {
                return new ConversationTerminalPresentation(
                    reply.Trim(),
                    IsFailed(kind, errorCode, errorMessage) ? "请求失败" : "回复完成",
                    IsError: IsFailed(kind, errorCode, errorMessage));
            }

            if (IsFailed(kind, errorCode, errorMessage))
            {
                if (root.TryGetProperty("diagnostic", out var diagnostic)
                    && diagnostic.ValueKind == JsonValueKind.Object
                    && ReadString(diagnostic, "causeCode") is { Length: > 0 } causeCode)
                {
                    var lines = new List<string>
                    {
                        "## 请求失败",
                        "",
                        $"- 错误代码：`{SanitizeInline(causeCode)}`",
                        $"- 原因：{ReadString(diagnostic, "causeShortCause") ?? errorMessage ?? "执行失败。"}",
                    };
                    AppendBudgetEvidence(lines, ReadString(diagnostic, "evidenceJson"));
                    if (ReadString(diagnostic, "remediationHint") is { Length: > 0 } hint)
                        lines.Add($"- 建议：{hint}");
                    foreach (var key in new[] { "errorId", "traceId" })
                        if (ReadString(diagnostic, key) is { Length: > 0 } id)
                            lines.Add($"- {key}：`{SanitizeInline(id)}`");
                    return new ConversationTerminalPresentation(string.Join("\n", lines),
                        ReadString(diagnostic, "causeTitle") ?? "请求失败", IsError: true);
                }
                var code = string.IsNullOrWhiteSpace(errorCode)
                    ? "execution_failed"
                    : SanitizeInline(errorCode);
                var reason = string.IsNullOrWhiteSpace(errorMessage)
                    ? "Agent 执行失败，未生成回复。"
                    : errorMessage.Trim();
                return new ConversationTerminalPresentation(
                    $"## 请求失败\n\n- 错误代码：`{code}`\n- 原因：{reason}\n\n请检查 Agent 配置和运行诊断后重试。",
                    "请求失败",
                    IsError: true);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsFailed(
        string? kind,
        string? errorCode,
        string? errorMessage)
        => string.Equals(kind, "Failed", StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(errorCode)
           || !string.IsNullOrWhiteSpace(errorMessage);

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SanitizeInline(string value)
        => value.Trim().Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private static void AppendBudgetEvidence(List<string> lines, string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson)) return;
        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            var evidence = document.RootElement;
            if (long.TryParse(ReadString(evidence, "estimated_request_text_bytes"), out var bytes)
                && long.TryParse(ReadString(evidence, "request_text_budget_bytes"), out var limit))
                lines.Add($"- 请求文本体积：{bytes:N0} 字节；上传预算：{limit:N0} 字节");
            if (long.TryParse(ReadString(evidence, "tool_definition_bytes"), out var toolBytes))
                lines.Add($"- 工具定义体积：{toolBytes:N0} 字节");
            if (ReadString(evidence, "compaction_reason") is { Length: > 0 and <= 128 } reason)
                lines.Add($"- 压缩诊断：`{SanitizeInline(reason)}`");
        }
        catch (JsonException) { /* Other diagnostic fields remain usable. */ }
    }
}

public sealed record ConversationTerminalPresentation(
    string Content,
    string Summary,
    bool IsError);
