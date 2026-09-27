using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-13 runtime node slice: reads Core's runtime registry and freezes/unfreezes nodes through the shared
/// application operation, so a native freeze is audited exactly like the HTTP one.
/// </summary>
internal sealed class DesktopRuntimeNodeSettings(IDesktopKernel kernel) : IRuntimeNodeSettings
{
    public Task<IReadOnlyList<RuntimeNode>> ListNodesAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("runtimeNodes.list", (scope, _) =>
        {
            var nodes = scope.Services.GetRequiredService<RuntimeRegistryService>().GetAll();
            return Task.FromResult((IReadOnlyList<RuntimeNode>)nodes.Select(Map).ToArray());
        }, cancellationToken);

    public Task FreezeAsync(string nodeId, string reason, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("runtimeNodes.freeze", async (scope, token) =>
        {
            var applied = await scope.Services.GetRequiredService<RuntimeNodeAdminService>()
                .FreezeAsync(nodeId, reason, LocalDesktopIdentity.UserId, token);
            if (!applied) throw new InvalidOperationException($"节点 '{nodeId}' 不存在。");
            return true;
        }, cancellationToken);

    public Task UnfreezeAsync(string nodeId, string reason, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("runtimeNodes.unfreeze", async (scope, token) =>
        {
            var applied = await scope.Services.GetRequiredService<RuntimeNodeAdminService>()
                .UnfreezeAsync(nodeId, reason, LocalDesktopIdentity.UserId, token);
            if (!applied) throw new InvalidOperationException($"节点 '{nodeId}' 不存在。");
            return true;
        }, cancellationToken);

    private static RuntimeNode Map(RuntimeNodeInfo node) => new(
        node.NodeId, node.Endpoint, node.Status.ToString(), node.LastHeartbeat, node.ActiveSessionCount,
        node.EmbeddedMode, node.HostType ?? "", node.IsFrozen,
        node.NativeCapabilities?.Select(capability => new RuntimeNodeCapability(
            capability.CapabilityId, capability.Name, capability.Description ?? "",
            capability.Category.ToString(), capability.RequiresApproval)).ToArray() ?? []);
}
