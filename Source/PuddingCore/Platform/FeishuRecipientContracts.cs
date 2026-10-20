namespace PuddingCode.Platform;

/// <summary>配置驱动的飞书收件人目录；不读取历史消息，不向工具暴露应用凭据。</summary>
public interface IFeishuRecipientDirectory
{
    Task<IReadOnlyList<FeishuRecipient>> ListAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct = default);
}

public sealed record FeishuRecipient(string ChannelId, string ChannelName, string OpenId)
{
    public string Address => $"user:{OpenId}";
    public string ConnectorId => FeishuConnectorIds.ForChannel(ChannelId);
}

public static class FeishuConnectorIds
{
    public static string ForChannel(string channelId) => $"feishu:{channelId}";
}
