namespace PuddingCode.Platform;

public static class SlackChannelMetadata
{
    public const string TeamId = "slack_team_id";
    public const string UserId = "slack_user_id";
    public const string ThreadTs = "slack_thread_ts";
    public const string ConversationKey = "slack_conversation_key";
    public const string IsDirectMessage = "slack_is_dm";
    public const string IsBot = "slack_sender_is_bot";
    public static string ConnectorId(string channelId) => "slack:" + channelId;
    public static string Principal(string teamId, string userId) => $"slack:{teamId}:{userId}";
}

public enum ConnectorDeliveryFailure { Retryable, Rejected, Unknown }

/// <summary>A send result that must not be collapsed into an ordinary retryable exception.</summary>
public sealed class ConnectorDeliveryException(ConnectorDeliveryFailure failure, string code,
    DateTimeOffset? retryAt = null) : Exception($"{failure}: {code}")
{
    public ConnectorDeliveryFailure Failure { get; } = failure;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
