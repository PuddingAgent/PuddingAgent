using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using PuddingPlatform.Services.Eventing;
using PuddingRuntime.Services.Plugins;

namespace PuddingAgent.Services;

/// <summary>
/// Host adapter from the platform event sink to the real plugin manager. Host-scoped activation identity ("host-dispatch")
/// is supplied by the sink; the manager only delivers to active, authorized subscribers in that workspace. The plugin may
/// request an archive through the authorized callback; the host derives the archive identity from its own event context.
/// </summary>
public sealed class HostWorkspacePluginEventTarget(ExecutablePluginService plugins, PluginArchiveStore archive)
    : IWorkspacePluginEventTarget
{
    public async ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
        string pluginId, long generation, CancellationToken ct = default)
    {
        var payloadRef = workspaceEvent.Payload.TryGetProperty("payloadRef", out var refNode)
            ? refNode.GetString() ?? ""
            : "";
        var payloadHash = workspaceEvent.Payload.TryGetProperty("payloadHash", out var hashNode)
            ? hashNode.GetString() ?? ""
            : "";
        async Task<JsonElement> Callback(string method, JsonElement arguments, CancellationToken token)
        {
            if (method != "plugin.archive.record")
                throw new PluginException("plugin.core_callback_denied", "core_callback",
                    "No other host capability is granted to an event callback.");
            var requestedEventId = arguments.TryGetProperty("eventId", out var idNode) ? idNode.GetString() : null;
            if (!string.Equals(requestedEventId, workspaceEvent.EventId, StringComparison.Ordinal))
                throw new PluginException("plugin.core_callback_denied", "core_callback",
                    "An observer may only archive the event it was delivered.");
            // Host-derived identity; the plugin cannot choose the workspace/plugin/artifact reference.
            var receipt = await archive.RecordAsync(new PluginArchiveRecord(workspaceEvent.EventId,
                workspaceEvent.EventId, workspaceEvent.Scope.WorkspaceId, pluginId, payloadRef, payloadHash),
                token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { accepted = true, archiveId = receipt.ArchiveId,
                replayed = receipt.Replayed });
        }

        return await plugins.Manager.PublishWorkspaceEventAsync(workspaceEvent, pluginId, generation, Callback, ct)
            .ConfigureAwait(false);
    }
}
