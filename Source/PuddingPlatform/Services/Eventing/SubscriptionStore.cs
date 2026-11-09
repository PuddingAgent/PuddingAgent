using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// PA-07A S5 subscription + checkpoint store: a grant creates one durable subscription with cursor 0; cancellation
/// is durable; acking deliveries and advancing the cursor happen in ONE transaction with a revision fence, and an
/// ack batch containing a foreign/unknown delivery id fails the whole batch (no event loss, no silent skip).
/// </summary>
public sealed class SubscriptionStore(IDbContextFactory<PlatformDbContext> dbFactory)
    : ISubscriptionStore, IEventCheckpointStore
{
    public async ValueTask<SubscriptionRecord> CreateAsync(SubscriptionGrant grant, CancellationToken ct = default)
    {
        if (grant is null || !grant.IsValid)
            throw Error(EventingErrorCodes.GrantMissing, "A valid host-bound subscription grant is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = new EventSubscriptionEntity
        {
            SubscriptionId = "sub-" + Guid.NewGuid().ToString("N"),
            PluginId = grant.PluginId,
            Generation = grant.Generation,
            WorkspaceId = grant.WorkspaceId,
            Kinds = string.Join(',', grant.Kinds),
            CursorSequence = 0,
            ScannedThroughSequence = 0,
            Revision = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EventSubscriptions.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async ValueTask<SubscriptionRecord> GetAsync(string subscriptionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return ToRecord(await FindAsync(db, subscriptionId, ct).ConfigureAwait(false));
    }

    public async ValueTask CancelAsync(string subscriptionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, subscriptionId, ct).ConfigureAwait(false);
        if (entity.Cancelled) return;
        entity.Cancelled = true;
        entity.Revision++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<SubscriptionRecord>> ListAsync(string workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded workspace id is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = (await db.EventSubscriptions.AsNoTracking().Where(s => s.WorkspaceId == workspaceId)
                .ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(s => s.CreatedAt).ToList();
        return [.. rows.Select(ToRecord)];
    }

    public async ValueTask<SubscriptionRecord> AckAndAdvanceAsync(string subscriptionId, string ownerId,
        ImmutableArray<string> deliveryIds, long nextSequence, long expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128
            || deliveryIds.IsDefaultOrEmpty || deliveryIds.Length > 256 || nextSequence < 0)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded, owned acknowledgement batch is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var subscription = await FindAsync(db, subscriptionId, ct).ConfigureAwait(false);
        if (subscription.Cancelled)
            throw Error(EventingErrorCodes.SubscriptionCancelled, "A cancelled subscription cannot advance.");
        if (subscription.Revision != expectedRevision)
            throw Error(EventingErrorCodes.CursorConflict, "The subscription revision changed.");
        if (nextSequence <= subscription.CursorSequence)
            throw Error(EventingErrorCodes.CursorGap, "The cursor must advance monotonically.");
        var ids = deliveryIds.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.EventDeliveries.Where(d => ids.Contains(d.DeliveryId)
            && d.SubscriptionId == subscriptionId).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count != ids.Length)
            throw Error(EventingErrorCodes.RequestInvalid, "The acknowledgement references a foreign or unknown delivery.");
        if (rows.Any(d => !string.Equals(d.LeaseOwnerId, ownerId, StringComparison.Ordinal)))
            throw Error(EventingErrorCodes.LeaseStale, "The acknowledgement owner does not hold the lease.");
        if (rows.Max(d => d.EventSequence) > nextSequence)
            throw Error(EventingErrorCodes.CursorGap, "The cursor is behind an acknowledged delivery.");
        await EnsureNoDeadLetterBlocksAsync(db, subscriptionId, nextSequence, ct).ConfigureAwait(false);
        db.EventDeliveries.RemoveRange(rows);
        subscription.CursorSequence = nextSequence;
        subscription.ScannedThroughSequence = Math.Max(subscription.ScannedThroughSequence, nextSequence);
        subscription.Revision++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ToRecord(subscription);
    }

    public async ValueTask<SubscriptionRecord> AdvanceScanAsync(string subscriptionId, long scannedThroughSequence,
        long expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || scannedThroughSequence < 0)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded scan advance is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var subscription = await FindAsync(db, subscriptionId, ct).ConfigureAwait(false);
        if (subscription.Cancelled)
            throw Error(EventingErrorCodes.SubscriptionCancelled, "A cancelled subscription cannot advance.");
        if (subscription.Revision != expectedRevision)
            throw Error(EventingErrorCodes.CursorConflict, "The subscription revision changed.");
        if (scannedThroughSequence <= subscription.ScannedThroughSequence
            || scannedThroughSequence < subscription.CursorSequence)
            throw Error(EventingErrorCodes.CursorGap, "The scan position must advance monotonically.");
        if (await db.EventDeliveries.AnyAsync(d => d.SubscriptionId == subscriptionId && !d.DeadLettered, ct)
                .ConfigureAwait(false))
            throw Error(EventingErrorCodes.CursorGap, "A pending delivery blocks scan progress.");
        await EnsureNoDeadLetterBlocksAsync(db, subscriptionId, scannedThroughSequence, ct).ConfigureAwait(false);
        subscription.ScannedThroughSequence = scannedThroughSequence;
        subscription.Revision++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(subscription);
    }

    private static async Task EnsureNoDeadLetterBlocksAsync(PlatformDbContext db, string subscriptionId,
        long sequence, CancellationToken ct)
    {
        var blocked = await db.EventDeliveries.Where(d => d.SubscriptionId == subscriptionId && d.DeadLettered
                && d.EventSequence <= sequence)
            .Select(d => (long?)d.EventSequence).MinAsync(ct).ConfigureAwait(false);
        if (blocked is not null)
            throw Error(EventingErrorCodes.DeadLetterBlocked, "A dead-lettered delivery blocks the checkpoint.");
    }

    private static async Task<EventSubscriptionEntity> FindAsync(PlatformDbContext db, string subscriptionId,
        CancellationToken ct)
        => await db.EventSubscriptions.SingleOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct)
            .ConfigureAwait(false)
            ?? throw Error(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");

    private static SubscriptionRecord ToRecord(EventSubscriptionEntity e) => new(e.SubscriptionId,
        new(e.PluginId, e.Generation, e.WorkspaceId, [.. e.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries)]),
        e.CursorSequence, e.Revision, e.CreatedAt, e.Cancelled, e.ScannedThroughSequence);

    private static EventingException Error(string code, string message) => new(code, message);
}
