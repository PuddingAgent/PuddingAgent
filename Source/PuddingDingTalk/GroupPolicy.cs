using System.Text;
using System.Text.Json;

namespace PuddingDingTalk;

public sealed record GroupSource(string CorporationId, string ConversationId, string UserId,
    DateTimeOffset SentAt, DateTimeOffset ReceivedAt)
{
    // No DingTalk-to-Pudding account bindings exist. Only a host-owned binding may change this.
    public string PuddingGroup => "guest";
}

public sealed record PublicQuestion(GroupSource Source, string Text, string MessageId);
public interface IPublicGroupAgent
{
    Task<string> ReplyAsync(PublicQuestion question, CancellationToken ct);
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

    internal static GroupEvent? Verify(JsonElement data, string appId, DateTimeOffset now)
    {
        var corp = String(data, "chatbotCorpId");
        var room = String(data, "conversationId");
        var user = String(data, "senderStaffId");
        var messageId = String(data, "msgId");
        if (String(data, "robotCode") != appId || !ValidId(corp) || String(data, "senderCorpId") != corp
            || String(data, "conversationType") != "2" || !ValidId(room) || !ValidId(user) || !ValidId(messageId)
            || !data.TryGetProperty("isInAtList", out var mention) || mention.ValueKind != JsonValueKind.True
            || String(data, "msgtype") != "text" || !data.TryGetProperty("text", out var body)
            || String(body, "content") is not { } text || string.IsNullOrWhiteSpace(text)
            || !data.TryGetProperty("createAt", out var sent) || sent.ValueKind != JsonValueKind.Number
            || !sent.TryGetInt64(out var ms) || ms < now.AddMinutes(-10).ToUnixTimeMilliseconds()
            || ms > now.AddMinutes(2).ToUnixTimeMilliseconds()
            || !Uri.TryCreate(String(data, "sessionWebhook"), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "oapi.dingtalk.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/robot/sendBySession"
            || !uri.Query.StartsWith("?session=", StringComparison.Ordinal) || uri.Query.Length <= 9)
            return null;
        var principal = new GroupSource(corp!, room!, user!, DateTimeOffset.FromUnixTimeMilliseconds(ms), now);
        var oversized = Encoding.UTF8.GetByteCount(text) > 4096;
        return new(new(principal, oversized ? "" : text, messageId!), messageId!, uri, oversized);
    }
}
