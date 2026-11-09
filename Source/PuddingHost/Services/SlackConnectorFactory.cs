using PuddingAgent.Connectors;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

public sealed class SlackConnectorFactory(AgentManifestCatalog manifests, ChannelConfigurationFileService channels,
    PuddingDataPaths paths, ILoggerFactory loggerFactory, ILogger<SlackConnectorFactory> logger)
{
    public async Task<IReadOnlyList<IPuddingConnector>> CreateAsync(CancellationToken ct = default)
    {
        var providers = (await channels.ListProvidersAsync(ct)).Where(p => p.IsEnabled && p.ChannelType == ChannelProviderKinds.Slack)
            .Select(p => p.ProviderId).ToHashSet(StringComparer.Ordinal);
        var agents = (await manifests.ListAsync(ct)).Where(a => a.IsEnabled && !a.IsFrozen).ToArray();
        var configured = (await channels.ListAllChannelsAsync(ct)).Where(c => c.IsEnabled && providers.Contains(c.ProviderId)).ToArray();
        var duplicates = configured.Where(c => c.Slack is not null).GroupBy(c => c.Slack!.AppId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var result = new List<IPuddingConnector>();
        foreach (var channel in configured)
        {
            var owners = agents.Where(a => a.WorkspaceId == channel.WorkspaceId && a.ChannelIds.Contains(channel.ChannelId, StringComparer.Ordinal)).ToArray();
            if (owners.Length != 1 || channel.Slack is null || duplicates.Contains(channel.Slack.AppId))
            {
                logger.LogWarning("[Slack] Channel skipped: requires one enabled Agent and an exclusive App ID channel={ChannelId}", channel.ChannelId);
                continue;
            }
            try
            {
                var settings = SlackChannelValidation.Normalize(channel.Slack);
                result.Add(new SlackConnector(channel.ChannelId, channel.WorkspaceId, owners[0].AgentInstanceId, settings,
                    Path.Combine(paths.ChannelRoot(channel.ChannelId), "slack"), loggerFactory.CreateLogger<SlackConnector>()));
            }
            catch (ArgumentException) { logger.LogWarning("[Slack] Invalid channel settings channel={ChannelId}", channel.ChannelId); }
        }
        return result;
    }
}
