using PuddingAgent.Connectors;
using PuddingCode.Platform;

namespace PuddingAgent.Services;

/// <summary>
/// Creates one Feishu connector for every enabled Agent-owned Feishu binding.
/// AppId uniqueness enforces that one robot is not consumed by multiple Agents.
/// </summary>
public sealed class FeishuConnectorFactory(
    AgentManifestCatalog manifests,
    ILoggerFactory loggerFactory,
    ILogger<FeishuConnectorFactory> logger)
{
    public async Task<IReadOnlyList<IPuddingConnector>> CreateAsync(
        CancellationToken ct = default)
    {
        var configured = (await manifests.ListAsync(ct))
            .Where(manifest =>
                manifest.IsEnabled
                && !manifest.IsFrozen
                && manifest.Feishu is { Enabled: true })
            .ToList();

        var invalid = configured
            .Where(manifest =>
                string.IsNullOrWhiteSpace(manifest.Feishu!.AppId)
                || string.IsNullOrWhiteSpace(manifest.Feishu.AppSecret))
            .ToList();
        foreach (var manifest in invalid)
        {
            logger.LogError(
                "[Feishu] Agent binding is enabled but incomplete agent={AgentId}",
                manifest.AgentInstanceId);
        }

        var valid = configured.Except(invalid).ToList();
        var duplicates = valid
            .GroupBy(
                manifest => manifest.Feishu!.AppId,
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        var duplicatedAppIds = duplicates
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in duplicates)
        {
            logger.LogError(
                "[Feishu] One robot AppId may only bind to one Agent; all conflicting bindings were skipped agents={AgentIds}",
                string.Join(
                    ", ",
                    duplicate.Select(manifest => manifest.AgentInstanceId)));
        }

        var connectors = valid
            .Where(manifest =>
                !duplicatedAppIds.Contains(manifest.Feishu!.AppId))
            .Select(manifest => (IPuddingConnector)new FeishuConnector(
                new FeishuConnectorBinding(
                    manifest.AgentInstanceId,
                    manifest.WorkspaceId,
                    manifest.Feishu!.AppId,
                    manifest.Feishu.AppSecret,
                    manifest.Feishu.Description),
                loggerFactory.CreateLogger<FeishuConnector>()))
            .ToList();

        logger.LogInformation(
            "[Feishu] Loaded {Count} Agent-owned connector binding(s)",
            connectors.Count);
        return connectors;
    }
}
