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
    FeishuTtsDeliveryService ttsDeliveryService,
    FeishuImageUploadPreparationService imageUploadPreparation,
    VisionArtifactStorageService visionArtifacts,
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
        logger.LogInformation(
            "[Feishu][diag] enabled feishu provider ids: [{Ids}]",
            string.Join(", ", enabledProviderIds));

        var allAgents = await manifests.ListAsync(ct);
        logger.LogInformation(
            "[Feishu][diag] all agents on disk: [{Agents}]",
            string.Join(
                " | ",
                allAgents.Select(a =>
                    a.AgentInstanceId
                    + "(enabled=" + a.IsEnabled
                    + ",frozen=" + a.IsFrozen
                    + ",channels=[" + string.Join(",", a.ChannelIds) + "])")));
        var agents = allAgents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToList();

        var allChannels = await channels.ListAllChannelsAsync(ct);
        logger.LogInformation(
            "[Feishu][diag] all channels on disk: [{Channels}]",
            string.Join(
                " | ",
                allChannels.Select(c =>
                    c.ChannelId
                    + "(provider=" + c.ProviderId
                    + ",enabled=" + c.IsEnabled
                    + ",workspace=" + c.WorkspaceId + ")")));
        var configuredChannels = allChannels
            .Where(channel =>
                channel.IsEnabled
                && enabledProviderIds.Contains(channel.ProviderId))
            .ToList();
        logger.LogInformation(
            "[Feishu][diag] configured feishu channels after filter: [{Channels}]",
            string.Join(", ", configuredChannels.Select(c => c.ChannelId)));

        var bindings = new List<(AgentInstanceManifest Agent, ChannelInstanceManifest Channel)>();
        foreach (var channel in configuredChannels)
        {
            var boundAgents = agents.Where(agent =>
                    string.Equals(agent.WorkspaceId, channel.WorkspaceId, StringComparison.Ordinal)
                    && agent.ChannelIds.Contains(channel.ChannelId, StringComparer.Ordinal))
                .ToList();
            logger.LogInformation(
                "[Feishu][diag] channel={ChannelId} matched bound agents: [{Agents}] count={Count}",
                channel.ChannelId,
                string.Join(", ", boundAgents.Select(a => a.AgentInstanceId)),
                boundAgents.Count);
            if (boundAgents.Count != 1)
            {
                // 跳过（不是致命）：禁用或已删除的 agent 实例会留下孤儿 channel。
                // 2026-10-28 实测：这里用 Error 会让人误判为「飞书断了」——实际只是该 channel 被跳过，
                // 其它 enabled 且已绑定的 channel 仍然正常收发。故降为 Warning 并明写 skipped。
                logger.LogWarning(
                    "[Feishu] Channel skipped: must be referenced by exactly one enabled Agent channel={ChannelId} agentCount={AgentCount} reason={Reason}",
                    channel.ChannelId,
                    boundAgents.Count,
                    boundAgents.Count == 0 ? "no-enabled-agent-binds-it" : "multiple-enabled-agents-bind-it");
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
            logger.LogInformation(
                "[Feishu][diag] channel={ChannelId} accepted binding agent={AgentId} appId={AppId}",
                channel.ChannelId,
                boundAgents[0].AgentInstanceId,
                settings.AppId);
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
                    binding.Channel.ChannelId,
                    binding.Channel.Feishu.TtsRepliesEnabled,
                    binding.Channel.Feishu.TtsVoice),
                loggerFactory.CreateLogger<FeishuConnector>(),
                inboundMessageMapper,
                ttsDeliveryService,
                imageUploadPreparation,
                visionArtifacts))
            .ToList();

        logger.LogInformation(
            "[Feishu] Loaded {Count} channel-owned connector binding(s)",
            connectors.Count);
        return connectors;
    }
}
