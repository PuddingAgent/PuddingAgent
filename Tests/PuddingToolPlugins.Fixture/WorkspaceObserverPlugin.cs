using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.PluginFixture;

/// <summary>Zero-tool workspace observer fixture: one subscription, requests the host archive through the authorized API.</summary>
public sealed class WorkspaceObserverPlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => [];
    public override IReadOnlyCollection<string> SubscriptionIds => ["archive"];

    public override ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
        => ValueTask.FromException<PluginResult>(
            new PluginException("plugin.not_found", "execute", "A subscription-only plugin has no tools."));

    public override async ValueTask OnWorkspaceEventAsync(PluginWorkspaceEvent workspaceEvent, CancellationToken ct)
    {
        if (Context is IPluginEventAudit audit)
            await audit.RequestArchiveAsync(workspaceEvent.EventId, ct);
    }
}

/// <summary>Zero-tool observer that consumes the event but deliberately does not request an archive.</summary>
public sealed class WorkspaceObserverPassivePlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => [];
    public override IReadOnlyCollection<string> SubscriptionIds => ["archive"];

    public override ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
        => ValueTask.FromException<PluginResult>(
            new PluginException("plugin.not_found", "execute", "A subscription-only plugin has no tools."));
}
