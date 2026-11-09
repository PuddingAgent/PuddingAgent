using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// Host adapter: the only place allowed to turn committed event deliveries into plugin workspace events. Platform owns
/// the mapping and the "no active subscriber" failure; the actual plugin activation/delivery stays in the runtime.
/// </summary>
public interface IWorkspacePluginEventTarget
{
    ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
        string pluginId, long generation, CancellationToken ct = default);
}

/// <summary>
/// Maps a committed <see cref="EventDelivery"/> to the frozen plugin workspace-event contract and delivers it to the
/// subscriber that owns the durable subscription (never to every matching plugin). A delivery with no active subscriber
/// fails instead of acknowledging, so the host can cancel the stale subscription rather than silently lose the event.
/// </summary>
public sealed class PluginManagerEventDeliverySink(IWorkspacePluginEventTarget target,
    ISubscriptionStore subscriptions) : IPluginEventDeliverySink
{
    public async ValueTask DeliverAsync(EventDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!string.Equals(delivery.Event.Kind, PluginEvents.WorkspaceArtifactCreated, StringComparison.Ordinal))
            return;

        SubscriptionRecord record;
        try
        {
            record = await subscriptions.GetAsync(delivery.SubscriptionId, ct).ConfigureAwait(false);
        }
        catch (EventingException ex) when (ex.Code is EventingErrorCodes.SubscriptionMissing)
        {
            throw;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            payloadRef = delivery.Event.PayloadRef,
            payloadHash = delivery.Event.PayloadHash,
            sequence = delivery.Event.Sequence,
            producer = delivery.Event.Producer,
            businessRef = delivery.Event.BusinessRef,
            causationId = delivery.Event.CausationId,
            correlationId = delivery.Event.CorrelationId,
        });
        var workspaceEvent = new PluginWorkspaceEvent(delivery.Event.EventId, delivery.Event.Kind,
            new PluginScope(delivery.Event.WorkspaceId, "", "", "host-dispatch"), payload,
            delivery.Event.OccurredAt);
        var deliveries = await target.DeliverAsync(workspaceEvent, record.Grant.PluginId,
            record.Grant.Generation, ct).ConfigureAwait(false);
        var accepted = deliveries.Where(d => d.Success).ToList();
        if (accepted.Count == 0)
            throw new EventingException(EventingErrorCodes.RequestInvalid,
                "No active plugin subscriber accepted the workspace event; cancel the stale subscription or retry.");
    }
}

/// <summary>Workspace ids that have committed outbox facts; one workspace is one distribution stream.</summary>
public sealed class OutboxWorkspaceSource(IDbContextFactory<PlatformDbContext> dbFactory) : IEventDispatchWorkspaceSource
{
    public async ValueTask<ImmutableArray<string>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await db.EventOutbox.AsNoTracking()
            .Where(e => e.WorkspaceId != "")
            .Select(e => e.WorkspaceId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. ids];
    }
}
