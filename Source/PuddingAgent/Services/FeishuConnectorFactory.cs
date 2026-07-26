using PuddingAgent.Connectors;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// Creates one Feishu connector for every enabled channel instance referenced
/// by exactly one enabled Agent. Credentials come only from data/channels.
/// </summary>
public sealed class FeishuConnectorFactory(
    AgentManifestCatalog manifests,
    ChannelConfigurationFileService channels,
    FeishuInboundMessageMapper inboundMessageMapper,
    ILoggerFactory loggerFactory,
    ILogger<FeishuConnectorFactory> logger)
{
    public async Task<IReadOnlyList<IPuddingConnector>> CreateAsync(
        CancellationToken ct = default)
    {
        var enabledProviderIds = (await channels.ListProvidersAsync(ct))
            .Where(provider =>
                provider.IsEnabled
                && string.Equals(
                    provider.ChannelType,
                    ChannelProviderKinds.Feishu,
                    StringComparison.OrdinalIgnoreCase))
            .Select(provider => provider.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agents = (await manifests.ListAsync(ct))
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToList();
        var configuredChannels = (await channels.ListAllChannelsAsync(ct))
            .Where(channel =>
                channel.IsEnabled
                && enabledProviderIds.Contains(channel.ProviderId))
            .ToList();

        var bindings = new List<(AgentInstanceManifest Agent, ChannelInstanceManifest Channel)>();
        foreach (var channel in configuredChannels)
        {
            var boundAgents = agents.Where(agent =>
                    string.Equals(agent.WorkspaceId, channel.WorkspaceId, StringComparison.Ordinal)
                    && agent.ChannelIds.Contains(channel.ChannelId, StringComparer.Ordinal))
                .ToList();
            if (boundAgents.Count != 1)
            {
                logger.LogError(
                    "[Feishu] Channel must be referenced by exactly one enabled Agent channel={ChannelId} agentCount={AgentCount}",
                    channel.ChannelId,
                    boundAgents.Count);
                continue;
            }
            if (channel.Feishu is not { } settings
                || string.IsNullOrWhiteSpace(settings.AppId)
                || string.IsNullOrWhiteSpace(settings.AppSecret))
            {
                logger.LogError(
                    "[Feishu] Channel credentials are incomplete channel={ChannelId}",
                    channel.ChannelId);
                continue;
            }
            bindings.Add((boundAgents[0], channel));
        }

        var duplicatedAppIds = bindings
            .GroupBy(binding => binding.Channel.Feishu!.AppId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var appId in duplicatedAppIds)
        {
            logger.LogError(
                "[Feishu] One AppId may only belong to one channel; conflicting channels were skipped channels={ChannelIds}",
                string.Join(
                    ", ",
                    bindings
                        .Where(binding => string.Equals(
                            binding.Channel.Feishu!.AppId,
                            appId,
                            StringComparison.Ordinal))
                        .Select(binding => binding.Channel.ChannelId)));
        }

        var connectors = bindings
            .Where(binding => !duplicatedAppIds.Contains(binding.Channel.Feishu!.AppId))
            .Select(binding => (IPuddingConnector)new FeishuConnector(
                new FeishuConnectorBinding(
                    binding.Agent.AgentInstanceId,
                    binding.Agent.WorkspaceId,
                    binding.Channel.Feishu!.AppId,
                    binding.Channel.Feishu.AppSecret,
                    binding.Channel.Description,
                    binding.Channel.ChannelId),
                loggerFactory.CreateLogger<FeishuConnector>(),
                inboundMessageMapper))
            .ToList();

        logger.LogInformation(
            "[Feishu] Loaded {Count} channel-owned connector binding(s)",
            connectors.Count);
        return connectors;
    }
}
