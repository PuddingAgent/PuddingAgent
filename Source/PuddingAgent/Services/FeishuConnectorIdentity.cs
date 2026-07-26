namespace PuddingAgent.Services;

public static class FeishuConnectorIdentity
{
    public static string ForChannel(string channelId) => $"feishu:{channelId}";

    [Obsolete("Connector identity is channel-owned.")]
    public static string ForAgent(string agentId) => ForChannel(agentId);
}
