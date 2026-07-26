using System.Text.Json;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 飞书消息 ↔ Pudding 消息格式转换。
/// </summary>
public static class MessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// 从飞书事件中提取纯文本内容。
    /// </summary>
    public static string ExtractText(this FeishuEvent evt)
    {
        // V2 格式
        if (evt.Event?.Message != null)
        {
            var msg = evt.Event.Message;
            if (!string.IsNullOrWhiteSpace(msg.TextWithoutAtBot))
                return msg.TextWithoutAtBot;
            if (!string.IsNullOrWhiteSpace(msg.Text))
                return msg.Text;

            // 解析 Content JSON
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                try
                {
                    var content = JsonSerializer.Deserialize<FeishuTextContent>(
                        msg.Content, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(content?.Text))
                        return content.Text;
                }
                catch { }
            }

            return $"[{msg.MessageType ?? "unknown"}]";
        }

        return "[empty]";
    }

    /// <summary>
    /// 提取发送者 ID。
    /// </summary>
    public static string? ExtractSenderId(this FeishuEvent evt)
    {
        return evt.Event?.Sender?.SenderId?.OpenId
            ?? evt.Event?.Sender?.SenderId?.UserId
            ?? evt.Event?.Sender?.SenderId?.UnionId;
    }

    /// <summary>
    /// 提取消息 ID。
    /// </summary>
    public static string? ExtractMessageId(this FeishuEvent evt)
    {
        return evt.Event?.Message?.MessageId
            ?? evt.Header?.EventId;
    }

    /// <summary>
    /// 提取会话 ID（ChatId）。
    /// </summary>
    public static string? ExtractChatId(this FeishuEvent evt)
    {
        return evt.Event?.Message?.ChatId;
    }
}
