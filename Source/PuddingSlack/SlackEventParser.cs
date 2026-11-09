using System.Text.Json;

namespace PuddingSlack;

public static class SlackEventParser
{
    public static SlackInboundMessage? Parse(JsonElement payload, SlackAdmission admission)
    {
        if (String(payload, "type") != "event_callback" || String(payload, "team_id") != admission.TeamId
            || String(payload, "api_app_id") != admission.AppId
            || !payload.TryGetProperty("event", out var evt)) return null;
        var type = String(evt, "type");
        var dm = type == "message" && String(evt, "channel_type") == "im";
        if (!dm && type != "app_mention") return null;
        var subtype = String(evt, "subtype");
        // Only explicitly allowed peer bots may mention us; edits and our own output never start turns.
        if (subtype is not null and not "bot_message") return null;
        var bot = subtype == "bot_message" || String(evt, "bot_id") is not null || evt.TryGetProperty("bot_profile", out _);
        var user = String(evt, "user");
        var channel = String(evt, "channel");
        var ts = String(evt, "ts");
        var eventId = String(payload, "event_id");
        var text = String(evt, "text");
        if (user is null || user == admission.BotUserId || channel is null || ts is null || eventId is null
            || string.IsNullOrWhiteSpace(text)) return null;
        // A configured bot identity stays a bot even if an event omits its bot-specific fields.
        bot |= admission.AllowedBotUserIds?.Contains(user, StringComparer.Ordinal) == true;
        if (bot ? dm || admission.AllowedBotUserIds?.Contains(user, StringComparer.Ordinal) != true
                : !admission.AllowedUserIds.Contains(user, StringComparer.Ordinal)) return null;
        if (!dm && !admission.AllowedChannelIds.Contains(channel, StringComparer.Ordinal)) return null;
        if (!dm && !text.Contains($"<@{admission.BotUserId}>", StringComparison.Ordinal)) return null;
        text = text.Replace($"<@{admission.BotUserId}>", "", StringComparison.Ordinal).Trim();
        if (text.Length == 0) return null;
        return new(eventId, admission.TeamId, channel, user, ts, String(evt, "thread_ts"), dm, text, bot);
    }

    public static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
