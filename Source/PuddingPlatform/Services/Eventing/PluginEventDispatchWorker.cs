using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using PuddingEventing;
using PuddingEventing.Contracts;

namespace PuddingPlatform.Services.Eventing;

/// <summary>Host-bound plugin delivery target. The PluginManager-backed implementation is wired separately.</summary>
public interface IPluginEventDeliverySink
{
    ValueTask DeliverAsync(EventDelivery delivery, CancellationToken ct = default);
}

/// <summary>Workspace ids the host has authorized this worker to scan (one workspace is one distribution stream).</summary>
public interface IEventDispatchWorkspaceSource
{
    ValueTask<ImmutableArray<string>> ListAsync(CancellationToken ct = default);
}

public sealed record EventDispatchBatchResult(int Subscriptions, int Delivered, int Acknowledged, int Failed,
    int DeadLettered, int SnapshotRequired);

/// <summary>
/// PA-07A S5 delivery loop: per workspace subscription, claim with this worker's owner token, invoke the plugin sink
/// in order, acknowledge only the contiguous succeeded prefix and leave failures leased for retry or dead-letter them
/// after the attempt cap. Another worker's live lease is never taken over. Correctness does not depend on tick timing.
/// </summary>
public sealed class PluginEventDispatchWorker(
    IEventDispatchWorkspaceSource workspaces,
    ISubscriptionStore subscriptions,
    SubscriptionDispatcher dispatcher,
    IEventDeliveryStore deliveries,
    IPluginEventDeliverySink sink,
    ILogger<PluginEventDispatchWorker>? logger = null,
    TimeProvider? clock = null)
{
    private readonly string _owner = "dispatch-" + Guid.NewGuid().ToString("N");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Owner => _owner;
    public const int MaxDeliveries = 32;
    public const int MaxAttempts = 5;

    public async Task<EventDispatchBatchResult> RunOnceAsync(int maxWorkspaces = 8, int maxDeliveries = MaxDeliveries,
        CancellationToken ct = default)
    {
        if (maxWorkspaces is < 1 or > 64 || maxDeliveries is < 1 or > SubscriptionDispatcher.MaxBatch)
            throw new EventingException(EventingErrorCodes.RequestInvalid, "The dispatch batch is out of bounds.");
        var workspacesToScan = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var subscriptionCount = 0;
        var delivered = 0;
        var acknowledged = 0;
        var failed = 0;
        var deadLettered = 0;
        var snapshotRequired = 0;
        foreach (var workspaceId in (workspacesToScan.IsDefault ? [] : workspacesToScan)
                     .Distinct(StringComparer.Ordinal).Take(maxWorkspaces))
        {
            foreach (var subscription in await subscriptions.ListAsync(workspaceId, ct).ConfigureAwait(false))
            {
                if (subscription.Cancelled) continue;
                subscriptionCount++;
                EventDispatchResult result;
                try
                {
                    result = await dispatcher.DispatchAsync(subscription.SubscriptionId, _owner, maxDeliveries, ct: ct)
                        .ConfigureAwait(false);
                }
                catch (EventingException ex) when (ex.Code is EventingErrorCodes.SubscriptionMissing
                                                       or EventingErrorCodes.SubscriptionCancelled)
                {
                    continue;
                }
                if (result.SnapshotRequired) { snapshotRequired++; continue; }
                if (result.Deliveries.IsDefaultOrEmpty) continue;

                var acked = ImmutableArray.CreateBuilder<string>();
                var stopped = false;
                foreach (var delivery in result.Deliveries.OrderBy(d => d.EventSequence))
                {
                    if (stopped) break;
                    try
                    {
                        await sink.DeliverAsync(delivery, ct).ConfigureAwait(false);
                        acked.Add(delivery.DeliveryId);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        stopped = true;
                        failed++;
                        var dead = delivery.Attempt + 1 >= MaxAttempts;
                        if (dead) deadLettered++;
                        await deliveries.FailAsync(subscription.SubscriptionId, delivery.DeliveryId, _owner,
                            ex is EventingException ev ? ev.Code : EventingErrorCodes.RequestInvalid, dead,
                            _clock.GetUtcNow(), ct).ConfigureAwait(false);
                        logger?.LogWarning(ex, "Plugin delivery failed for subscription {SubscriptionId} delivery {DeliveryId}.",
                            subscription.SubscriptionId, delivery.DeliveryId);
                    }
                }
                if (acked.Count == 0) continue;
                delivered += acked.Count;
                try
                {
                    await dispatcher.AckAsync(subscription.SubscriptionId, _owner, result.Deliveries, acked.ToImmutable(), ct)
                        .ConfigureAwait(false);
                    acknowledged += acked.Count;
                }
                catch (EventingException ex)
                {
                    // The claim was lost (expired lease or concurrent checkpoint); the events stay leased and retry.
                    logger?.LogWarning(ex, "Delivery acknowledgement was rejected for subscription {SubscriptionId}.",
                        subscription.SubscriptionId);
                }
            }
        }
        return new(subscriptionCount, delivered, acknowledged, failed, deadLettered, snapshotRequired);
    }
}
