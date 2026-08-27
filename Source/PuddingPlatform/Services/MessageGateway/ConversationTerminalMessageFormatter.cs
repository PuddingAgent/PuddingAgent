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

            if (string.Equals(kind, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new ConversationTerminalPresentation(
                    "请求已取消，Agent 未生成回复。",
                    "请求已取消",
                    IsError: false);
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
        => value.Trim().Replace('`', '\'');
}

public sealed record ConversationTerminalPresentation(
    string Content,
    string Summary,
    bool IsError);
