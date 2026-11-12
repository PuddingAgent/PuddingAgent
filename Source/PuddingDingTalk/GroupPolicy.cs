using System.Text;
using System.Text.Json;

namespace PuddingDingTalk;

public sealed record GroupSource(string CorporationId, string ConversationId, string UserId,
    DateTimeOffset SentAt, DateTimeOffset ReceivedAt)
{
    /// <summary>
    /// True for a group conversation, false for a private one-to-one chat.
    /// Only a private chat has a single audience, so it is the only place where echoing the
    /// caller's own identifiers back is safe; a group reply would expose them to every member.
    /// </summary>
    public bool Group { get; init; } = true;

}

public sealed record PublicQuestion(GroupSource Source, string Text, string MessageId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ImageDownloadCodes { get; init; } = [];
    public string? GroupTitle { get; init; }
}
public sealed record GroupImage(byte[] Bytes, string MimeType);
public sealed record GroupReply(string Text, IReadOnlyList<GroupImage>? Images = null);
public interface IPublicGroupAgent
{
    Task<GroupReply> ReplyAsync(PublicQuestion question, CancellationToken ct);
}

public static class PublicGroupAnswers
{
    public const string Refusal = "我可以介绍 MPCAL 的公开用法，也可以聊日常话题。涉及内部信息或实际操作，请使用已认证的私有入口。";
    public const string TooLong = "这条消息太长，请用一小段文字重新描述问题，并去掉敏感信息。";

    public static string ExtractRules(string document)
    {
        const string start = "<!-- pudding:dingtalk-group:v1:begin -->";
        const string end = "<!-- pudding:dingtalk-group:v1:end -->";
        var i = document.IndexOf(start, StringComparison.Ordinal);
        var j = document.IndexOf(end, StringComparison.Ordinal);
        if (i < 0 || j <= i + start.Length || j - i > 4096
            || i != document.LastIndexOf(start, StringComparison.Ordinal)
            || j != document.LastIndexOf(end, StringComparison.Ordinal))
            throw new InvalidDataException("Missing or ambiguous public group policy.");
        return document[(i + start.Length)..j].Trim();
    }
}

// Constructed only after validating a CALLBACK on the authenticated enterprise app Stream.
internal sealed record GroupEvent(PublicQuestion Question, string MessageId, Uri ReplyUrl, bool Oversized);

internal static class GroupIngress
{
    internal const string Topic = "/v1.0/im/bot/messages/get";
    internal static string? String(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 256 }
        && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Verifies an authenticated enterprise-app Stream callback. Group mentions and private one-to-one
    /// messages share this callback; only the group form carries an @-list to satisfy.
    /// Returns the parsed event, or a null event plus the first failing rule so the caller can log a
    /// diagnosable status instead of an opaque denial.
    /// </summary>
    internal static (GroupEvent? Event, string Reason) Verify(JsonElement data, string appId, DateTimeOffset now)
    {
        var corp = String(data, "chatbotCorpId");
        var room = String(data, "conversationId");
        var user = String(data, "senderStaffId");
        var messageId = String(data, "msgId");
        var conversationType = String(data, "conversationType");
        if (conversationType is not ("1" or "2")) return (null, "conversation_type");
        var group = conversationType == "2";
        if (String(data, "robotCode") != appId) return (null, "robot_code");
        if (!ValidId(corp)) return (null, "corp_id");
        if (String(data, "senderCorpId") != corp) return (null, "sender_corp");
        if (!ValidId(room)) return (null, "conversation_id");
        if (!ValidId(user)) return (null, "sender_id");
        if (!ValidId(messageId)) return (null, "message_id");
        // Only a group message must mention the bot; a private conversation has no @-list.
        if (group && (data.TryGetProperty("isInAtList", out var mention) is false
            || mention.ValueKind != JsonValueKind.True)) return (null, "not_mentioned");
        if (!data.TryGetProperty("createAt", out var sent) || sent.ValueKind != JsonValueKind.Number
            || !sent.TryGetInt64(out var ms)) return (null, "sent_at");
        if (ms < now.AddMinutes(-10).ToUnixTimeMilliseconds()
            || ms > now.AddMinutes(2).ToUnixTimeMilliseconds()) return (null, "stale");
        if (!Uri.TryCreate(String(data, "sessionWebhook"), UriKind.Absolute, out var uri))
            return (null, "webhook_url");
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "oapi.dingtalk.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/robot/sendBySession"
            || !uri.Query.StartsWith("?session=", StringComparison.Ordinal) || uri.Query.Length <= 9)
            return (null, "webhook_scope");
        var principal = new GroupSource(corp!, room!, user!, DateTimeOffset.FromUnixTimeMilliseconds(ms), now)
        {
            Group = group
        };
        var codes = new List<string>();
        string? text;
        var type = String(data, "msgtype");
        if (type == "text" && data.TryGetProperty("text", out var body)) text = String(body, "content");
        else if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            if (type == "picture" && String(content, "downloadCode") is { } picture)
            {
                codes.Add(picture);
                text = "";
            }
            else if (type == "richText" && content.TryGetProperty("richText", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                var combined = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (String(part, "text") is { } t) combined.Append(t);
                    if (String(part, "downloadCode") is { } code
                        && String(part, "type") is null or "picture" or "image") codes.Add(code);
                }
                text = combined.ToString();
            }
            else return (null, "content_type");
        }
        else return (null, "msgtype");
        if (string.IsNullOrWhiteSpace(text) && codes.Count == 0) return (null, "empty");
        if (codes.Any(code => code.Length is 0 or > 4096 || code.Any(char.IsControl)))
            return (null, "download_code");
        text = text?.Trim() ?? "";
        var oversized = Encoding.UTF8.GetByteCount(text) > 4096 || codes.Count > 4;
        var title = String(data, "conversationTitle");
        return (new(new(principal, oversized ? "" : text, messageId!)
        {
            ImageDownloadCodes = oversized ? [] : codes.Distinct(StringComparer.Ordinal).ToArray(),
            GroupTitle = title is { Length: > 0 and <= 80 } && !title.Any(char.IsControl) ? title : null
        }, messageId!, uri, oversized), "accepted");
    }
}
