using System.Text;
using System.Text.Json;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// Converts Feishu post/rich-text message content into Agent-readable Markdown.
/// </summary>
internal static class FeishuPostContentConverter
{
    internal const string EmptyPostMessage =
        "用户从飞书发送了一条富文本消息，但其中没有可提取的文本内容。";

    private static readonly string[] PreferredLocales =
    [
        "zh_cn",
        "en_us",
        "ja_jp",
    ];

    public static string ConvertToMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return EmptyPostMessage;

        try
        {
            using var document = JsonDocument.Parse(content);
            var payload = SelectPayload(document.RootElement);
            var title = ReadString(payload, "title");
            var body = RenderBody(payload, "content_v2");
            if (string.IsNullOrWhiteSpace(body))
                body = RenderBody(payload, "content");
            if (string.IsNullOrWhiteSpace(body))
                body = ExtractPlainText(payload);

            var markdown = CombineTitleAndBody(title, body);
            return string.IsNullOrWhiteSpace(markdown)
                ? EmptyPostMessage
                : markdown;
        }
        catch (JsonException)
        {
            return EmptyPostMessage;
        }
    }

    internal static JsonElement SelectPayload(JsonElement root)
    {
        if (HasPostShape(root))
            return root;

        if (root.ValueKind != JsonValueKind.Object)
            return root;

        foreach (var locale in PreferredLocales)
        {
            if (root.TryGetProperty(locale, out var localized)
                && HasPostShape(localized))
            {
                return localized;
            }
        }

        foreach (var property in root.EnumerateObject())
        {
            if (HasPostShape(property.Value))
                return property.Value;
        }

        return root;
    }

    private static bool HasPostShape(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        return element.TryGetProperty("title", out _)
            || element.TryGetProperty("content", out _)
            || element.TryGetProperty("content_v2", out _);
    }

    private static string RenderBody(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return RenderElement(content);

        var paragraphs = new List<string>();
        foreach (var paragraph in content.EnumerateArray())
            paragraphs.Add(RenderElement(paragraph).TrimEnd());

        return TrimBlankLines(string.Join("\n", paragraphs));
    }

    private static string RenderElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => string.Concat(
                element.EnumerateArray().Select(RenderElement)),
            JsonValueKind.Object => RenderObject(element),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string RenderObject(JsonElement element)
    {
        var tag = ReadString(element, "tag").ToLowerInvariant();
        return tag switch
        {
            "text" => ApplyStyles(
                ReadString(element, "text"),
                element),
            "a" => RenderLink(element),
            "at" => RenderMention(element),
            "code_block" => RenderCodeBlock(element),
            "md" or "markdown" => ReadString(element, "content"),
            "hr" => "---",
            "img" => "[图片]",
            "media" => "[媒体]",
            "emotion" => RenderEmotion(element),
            _ => ReadString(element, "text"),
        };
    }

    private static string RenderLink(JsonElement element)
    {
        var text = ApplyStyles(ReadString(element, "text"), element);
        var href = ReadString(element, "href");
        if (string.IsNullOrWhiteSpace(href))
            return text;
        if (string.IsNullOrWhiteSpace(text))
            return href;

        return $"[{EscapeLinkText(text)}]({EscapeLinkTarget(href)})";
    }

    private static string RenderMention(JsonElement element)
    {
        var name = ReadString(element, "user_name");
        if (string.IsNullOrWhiteSpace(name))
            name = ReadString(element, "text");
        if (string.IsNullOrWhiteSpace(name))
            name = ReadString(element, "user_id");
        if (string.IsNullOrWhiteSpace(name))
            return "@用户";

        return name.StartsWith('@') ? name : $"@{name}";
    }

    private static string RenderCodeBlock(JsonElement element)
    {
        var text = ReadString(element, "text");
        if (string.IsNullOrWhiteSpace(text))
            text = ReadString(element, "content");
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var language = ReadString(element, "language");
        return $"```{language}\n{text.TrimEnd()}\n```";
    }

    private static string RenderEmotion(JsonElement element)
    {
        var emojiType = ReadString(element, "emoji_type");
        return string.IsNullOrWhiteSpace(emojiType)
            ? "[表情]"
            : $":{emojiType}:";
    }

    private static string ApplyStyles(string text, JsonElement element)
    {
        if (string.IsNullOrEmpty(text)
            || !element.TryGetProperty("style", out var styles)
            || styles.ValueKind != JsonValueKind.Array)
        {
            return text;
        }

        var styleSet = styles
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (styleSet.Contains("code"))
            text = $"`{text.Replace("`", "\\`")}`";
        if (styleSet.Contains("bold"))
            text = $"**{text}**";
        if (styleSet.Contains("italic"))
            text = $"*{text}*";
        if (styleSet.Contains("lineThrough")
            || styleSet.Contains("strikethrough"))
        {
            text = $"~~{text}~~";
        }

        return text;
    }

    private static string ExtractPlainText(JsonElement payload)
    {
        var source = SelectPlainTextSource(payload);
        var values = new List<string>();
        CollectPlainText(source, values);
        return string.Join(
            "\n",
            values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static JsonElement SelectPlainTextSource(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload;

        if (payload.TryGetProperty("content_v2", out var contentV2)
            && contentV2.ValueKind == JsonValueKind.Array
            && contentV2.GetArrayLength() > 0)
        {
            return contentV2;
        }

        if (payload.TryGetProperty("content", out var content))
            return content;

        return payload;
    }

    private static void CollectPlainText(
        JsonElement element,
        List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectPlainText(item, values);
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    values.Add(text.GetString() ?? string.Empty);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("text"))
                        continue;
                    if (property.Value.ValueKind is JsonValueKind.Array
                        or JsonValueKind.Object)
                    {
                        CollectPlainText(property.Value, values);
                    }
                }

                break;
        }
    }

    private static string CombineTitleAndBody(string title, string body)
    {
        title = title.Trim();
        body = body.Trim();
        if (title.Length == 0)
            return body;
        if (body.Length == 0)
            return $"# {title}";

        return $"# {title}\n\n{body}";
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string TrimBlankLines(string value)
        => value.Trim('\r', '\n', ' ', '\t');

    private static string EscapeLinkText(string value)
        => value.Replace("[", "\\[").Replace("]", "\\]");

    private static string EscapeLinkTarget(string value)
        => value.Replace(")", "\\)");
}
