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
    /// 从飞书事件中提取 Agent 可读内容；post 富文本转换为 Markdown。
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
                if (string.Equals(
                        msg.MessageType,
                        "post",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return FeishuPostContentConverter.ConvertToMarkdown(
                        msg.Content);
                }

                try
                {
                    var content = JsonSerializer.Deserialize<FeishuTextContent>(
                        msg.Content, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(content?.Text))
                        return content.Text;
                }
                catch { }
            }

            if (string.Equals(
                    msg.MessageType,
                    "post",
                    StringComparison.OrdinalIgnoreCase))
            {
                return FeishuPostContentConverter.EmptyPostMessage;
            }

            return $"[{msg.MessageType ?? "unknown"}]";
        }

        return "[empty]";
    }

    /// <summary>Extracts image_key from an image message.</summary>
    public static string? ExtractImageKey(this FeishuEvent evt)
    {
        var message = evt.Event?.Message;
        if (message is null
            || !string.Equals(message.MessageType, "image", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(message.Content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FeishuImageContent>(
                message.Content,
                JsonOptions)?.ImageKey;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts image_key values from a post (rich text) message's content_v2
    /// (preferred) or content JSON, preserving element order. Returns an empty
    /// list when the message is not a post/text message or contains no img
    /// elements.
    /// </summary>
    public static List<string> ExtractPostImageKeys(this FeishuEvent evt)
    {
        var message = evt.Event?.Message;
        if (message is null
            || (!string.Equals(
                    message.MessageType,
                    "post",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    message.MessageType,
                    "text",
                    StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(message.Content))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(message.Content);
            var payload = FeishuPostContentConverter.SelectPayload(
                document.RootElement);
            var keys = new List<string>();
            CollectPostImageKeys(payload, keys);
            return keys;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CollectPostImageKeys(
        JsonElement payload,
        List<string> keys)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            CollectPostImageKeysRecursive(payload, keys);
            return;
        }

        var collected = new List<string>();

        // Prefer content_v2 (new format); fall back to content (legacy).
        if (payload.TryGetProperty("content_v2", out var contentV2)
            && contentV2.ValueKind == JsonValueKind.Array)
        {
            CollectPostImageKeysRecursive(contentV2, collected);
            if (collected.Count > 0)
            {
                keys.AddRange(collected);
                return;
            }
        }

        if (payload.TryGetProperty("content", out var content))
        {
            CollectPostImageKeysRecursive(content, collected);
            if (collected.Count > 0)
            {
                keys.AddRange(collected);
                return;
            }
        }

        // Fallback: scan the whole payload (localized/malformed shapes).
        CollectPostImageKeysRecursive(payload, keys);
    }

    private static void CollectPostImageKeysRecursive(
        JsonElement element,
        List<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectPostImageKeysRecursive(item, keys);
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("tag", out var tag)
                    && tag.ValueKind == JsonValueKind.String
                    && string.Equals(
                        tag.GetString(),
                        "img",
                        StringComparison.OrdinalIgnoreCase)
                    && element.TryGetProperty("image_key", out var imageKey)
                    && imageKey.ValueKind == JsonValueKind.String)
                {
                    var key = imageKey.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                        keys.Add(key);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Array
                        or JsonValueKind.Object)
                    {
                        CollectPostImageKeysRecursive(
                            property.Value,
                            keys);
                    }
                }
                break;
        }
    }

    /// <summary>Extracts file_key from an audio or file message.</summary>
    public static string? ExtractFileKey(this FeishuEvent evt)
    {
        var message = evt.Event?.Message;
        if (message is null
            || (!string.Equals(
                    message.MessageType,
                    "audio",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    message.MessageType,
                    "file",
                    StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(message.Content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FeishuFileContent>(
                message.Content,
                JsonOptions)?.FileKey;
        }
        catch (JsonException)
        {
            return null;
        }
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
