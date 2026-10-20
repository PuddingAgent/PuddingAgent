using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>从当前 Agent 唯一绑定的启用渠道读取收件人；每次查询使用最新配置。</summary>
public sealed class FeishuRecipientDirectory(
    IWorkspaceAgentCatalog agents,
    ChannelConfigurationFileService channels) : IFeishuRecipientDirectory
{
    public async Task<IReadOnlyList<FeishuRecipient>> ListAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct = default)
    {
        var enabledAgents = (await agents.ListAgentsAsync(workspaceId, ct))
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToList();
        var agent = enabledAgents.SingleOrDefault(item => item.AgentId == agentInstanceId);
        if (agent is null)
            return [];

        var providers = (await channels.ListProvidersAsync(ct))
            .Where(provider => provider.IsEnabled
                && string.Equals(provider.ChannelType, ChannelProviderKinds.Feishu,
                    StringComparison.OrdinalIgnoreCase))
            .Select(provider => provider.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configured = await channels.ListAllChannelsAsync(ct);
        var result = new List<FeishuRecipient>();
        foreach (var channel in configured.Where(channel =>
                     channel.WorkspaceId == workspaceId && channel.IsEnabled
                     && providers.Contains(channel.ProviderId)
                     && agent.ChannelIds?.Contains(channel.ChannelId, StringComparer.Ordinal) == true))
        {
            if (enabledAgents.Count(item =>
                    item.ChannelIds?.Contains(channel.ChannelId, StringComparer.Ordinal) == true) != 1
                || channel.Feishu is not { } settings
                || string.IsNullOrWhiteSpace(settings.AppId)
                || string.IsNullOrWhiteSpace(settings.AppSecret))
                continue;

            // 配置冲突不能通过工具选择绕过连接器工厂的唯一 AppId 约束。
            if (configured.Count(item => item.IsEnabled
                    && providers.Contains(item.ProviderId)
                    && item.Feishu?.AppId == settings.AppId) != 1)
                continue;

            result.AddRange(settings.PrivilegedUserOpenIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Select(id => new FeishuRecipient(channel.ChannelId, channel.Name, id)));
        }

        return result.OrderBy(item => item.ChannelId, StringComparer.Ordinal)
            .ThenBy(item => item.OpenId, StringComparer.Ordinal).ToList();
    }
}
