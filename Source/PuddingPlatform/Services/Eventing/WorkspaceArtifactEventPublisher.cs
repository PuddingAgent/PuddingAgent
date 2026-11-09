using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// Host fact source: a versioned workspace artifact submission commits its business fact and the
/// <see cref="PluginEvents.WorkspaceArtifactCreated"/> outbox event in one transaction. The committed event is then
/// distributed by <see cref="PluginEventDispatchWorker"/>; plugins never poll the workspace directly.
/// </summary>
public sealed class WorkspaceArtifactEventPublisher(EventOutboxUnitOfWork outbox, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<string> PublishCreatedAsync(string workspaceId, string artifactRef, string payloadHash,
        string producer, string? businessRef = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        var eventId = Guid.NewGuid().ToString("N");
        await outbox.ExecuteAsync(async (db, view, token) =>
        {
            await view.AppendAsync(new OutboxFact(eventId, workspaceId, PluginEvents.WorkspaceArtifactCreated, 1,
                workspaceId, artifactRef, payloadHash, producer, _clock.GetUtcNow(), BusinessRef: businessRef), token);
            await db.SaveChangesAsync(token);
            return true;
        }, ct).ConfigureAwait(false);
        return eventId;
    }
}
