using System.Collections.Immutable;
using Pudding.ToolPlugins.Contracts;
using PuddingPlatform.Services.Eventing;
using PuddingRuntime.Services.Plugins;

namespace PuddingAgent.Services;

/// <summary>
/// Reads active plugin activations and their frozen manifests to produce subscription candidates. Only host-scoped or
/// workspace-scoped activations with declared subscriptions participate; the platform never sees the plugin catalog.
/// </summary>
public sealed class HostActivePluginSubscriptionSource(ExecutablePluginService plugins) : IActivePluginSubscriptionSource
{
    public ValueTask<ImmutableArray<PluginSubscriptionCandidate>> ListAsync(CancellationToken ct = default)
    {
        var candidates = ImmutableArray.CreateBuilder<PluginSubscriptionCandidate>();
        foreach (var activation in plugins.Manager.List().Where(a => a.State == "Active"))
        {
            PluginPackage package;
            try
            {
                package = plugins.Find(activation.ContentHash);
            }
            catch (PluginException)
            {
                continue;
            }
            if (package.Manifest.Subscriptions.Length == 0)
                continue;
            var kinds = package.Manifest.Subscriptions
                .Select(s => s.Event)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            candidates.Add(new(activation.Scope.WorkspaceId, activation.PluginId,
                Math.Max(1, activation.Revision), kinds));
        }
        return ValueTask.FromResult(candidates.ToImmutable());
    }
}
