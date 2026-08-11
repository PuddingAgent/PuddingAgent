namespace PuddingAgent.Services;

public static class FeishuConnectorIdentity
{
    public static string ForChannel(string channelId) => $"feishu:{channelId}";


}
