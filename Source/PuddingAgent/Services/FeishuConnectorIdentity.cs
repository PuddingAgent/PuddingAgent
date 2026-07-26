namespace PuddingAgent.Services;

public static class FeishuConnectorIdentity
{
    public static string ForAgent(string agentId) => $"feishu:{agentId}";
}
