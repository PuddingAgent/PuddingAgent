using System.Collections.Immutable;
using PuddingEventing.Contracts;

namespace PuddingPlatform.Services.Eventing;

/// <summary>Active plugin contribution known to the host: only these may hold a workspace-event subscription.</summary>
public sealed record PluginSubscriptionCandidate(string WorkspaceId, string PluginId, long Generation,
    ImmutableArray<string> Kinds);

/// <summary>
/// Keeps durable subscriptions aligned with active plugin activations: load creates one grant, reload/generation change
/// replaces it, unload cancels it. Synchronization is idempotent and never touches another plugin's grant.
/// </summary>
public sealed class PluginSubscriptionSynchronizer(ISubscriptionStore store)
{
    public async Task<SubscriptionSyncResult> SyncAsync(IReadOnlyList<PluginSubscriptionCandidate> candidates,
        IEnumerable<string>? knownWorkspaces = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var desired = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.WorkspaceId) && !string.IsNullOrWhiteSpace(c.PluginId)
                && c.Generation >= 1 && !c.Kinds.IsDefaultOrEmpty && c.Kinds.All(EventEnvelope.Valid))
            .GroupBy(c => c.WorkspaceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // Cancellation needs the workspace even when no candidate remains (unloaded plugin): the host supplies the
        // workspaces it currently dispatches for.
        foreach (var workspaceId in (knownWorkspaces ?? []).Where(w => !string.IsNullOrWhiteSpace(w)))
            desired.TryAdd(workspaceId, []);
        var created = 0;
        var cancelled = 0;
        foreach (var (workspaceId, wanted) in desired)
        {
            var existing = await store.ListAsync(workspaceId, ct).ConfigureAwait(false);
            foreach (var candidate in wanted)
            {
                var current = existing.FirstOrDefault(s => !s.Cancelled
                    && string.Equals(s.Grant.PluginId, candidate.PluginId, StringComparison.Ordinal));
                if (current is not null
                    && current.Grant.Generation == candidate.Generation
                    && current.Grant.Kinds.Order().SequenceEqual(candidate.Kinds.Order()))
                    continue;
                if (current is not null)
                {
                    await store.CancelAsync(current.SubscriptionId, ct).ConfigureAwait(false);
                    cancelled++;
                }
                await store.CreateAsync(new SubscriptionGrant(candidate.PluginId, candidate.Generation,
                    candidate.WorkspaceId, candidate.Kinds), ct).ConfigureAwait(false);
                created++;
            }
            foreach (var stale in existing.Where(s => !s.Cancelled
                         && !wanted.Any(c => string.Equals(c.PluginId, s.Grant.PluginId, StringComparison.Ordinal))))
            {
                await store.CancelAsync(stale.SubscriptionId, ct).ConfigureAwait(false);
                cancelled++;
            }
        }
        return new(created, cancelled);
    }
}

public sealed record SubscriptionSyncResult(int Created, int Cancelled);

/// <summary>Host-bound active contribution inventory. The platform never enumerates the plugin catalog itself.</summary>
public interface IActivePluginSubscriptionSource
{
    ValueTask<ImmutableArray<PluginSubscriptionCandidate>> ListAsync(CancellationToken ct = default);
}

/// <summary>Call after a plugin load/unload (or periodically) to align subscriptions with active activations.</summary>
public sealed class PluginSubscriptionLifecycle(IActivePluginSubscriptionSource source,
    PluginSubscriptionSynchronizer synchronizer)
{
    public async Task<SubscriptionSyncResult> SyncAsync(IEnumerable<string> knownWorkspaces,
        CancellationToken ct = default)
    {
        var candidates = await source.ListAsync(ct).ConfigureAwait(false);
        return await synchronizer.SyncAsync(candidates, knownWorkspaces, ct).ConfigureAwait(false);
    }
}
