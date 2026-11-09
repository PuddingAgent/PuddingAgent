namespace PuddingSlack;

/// <summary>Secrets deliberately have no generated record ToString.</summary>
public sealed class SlackCredentials
{
    public required string AppToken { get; set; }
    public required string BotToken { get; set; }
    public string? AppRefreshToken { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public DateTimeOffset? AppTokenExpiresAt { get; set; }
    public string? PendingRefreshTokenHash { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
    public SlackCredentials Copy() => (SlackCredentials)MemberwiseClone();
    public SlackCredentials Validate()
    {
        // Tokens are opaque. The endpoint determines token type; never strip xoxe. or another prefix.
        if (!Usable(AppToken) || !Usable(BotToken)) throw new SlackProtocolException("credential_file_invalid");
        return this;
    }
    private static bool Usable(string? token) => !string.IsNullOrWhiteSpace(token)
        && !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    public override string ToString() => "SlackCredentials [redacted]";
}

public sealed record SlackIdentity(string TeamId, string BotUserId);
public sealed record SlackAdmission(string TeamId, string BotUserId, IReadOnlyList<string> AllowedUserIds,
    IReadOnlyList<string> AllowedChannelIds, string AppId, IReadOnlyList<string>? AllowedBotUserIds = null);
public sealed record SlackInboundMessage(string EventId, string TeamId, string ChannelId, string UserId,
    string MessageTs, string? ThreadTs, bool IsDirectMessage, string Text, bool IsBot = false)
{
    // message.im and app_mention may describe the same message. Deduplicate the message, not its delivery.
    public string MessageKey => $"{TeamId}/{ChannelId}/{MessageTs}";
    public string ConversationKey => $"{TeamId}/{ChannelId}/{(IsDirectMessage ? "dm" : ThreadTs ?? MessageTs)}";
    public string? ReplyThreadTs => IsDirectMessage ? ThreadTs : ThreadTs ?? MessageTs;
}

public enum SlackSendStatus { Sent, Retryable, Rejected, Unknown }
/// <summary>Sent means Slack acknowledged a channel/ts, never that a human read the message.</summary>
public sealed record SlackSendReceipt(SlackSendStatus Status, string Code, string? ChannelId = null,
    string? MessageTs = null, DateTimeOffset? RetryAt = null);
public sealed record SlackPost(string ChannelId, string? ThreadTs, string Text);
public sealed class SlackProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
