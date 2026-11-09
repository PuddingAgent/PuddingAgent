using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// PA-07A S5 delivery store: enqueue dedupes by (subscription, event id); claiming uses a persistent owner/lease CAS so
/// two workers never both hold the same delivery (a live lease owned by another owner is not re-leased; an expired
/// lease or the same owner may be). Failure/dead-letter transitions are owner-fenced; dead letters never appear in
/// pending/claim results until explicitly requeued.
/// </summary>
public sealed class EventDeliveryStore(IDbContextFactory<PlatformDbContext> dbFactory) : IEventDeliveryStore
{
    public async ValueTask EnqueueAsync(string subscriptionId, ImmutableArray<EventEnvelope> events,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || events.IsDefault)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded subscription and event batch are required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = (await db.EventDeliveries.Where(d => d.SubscriptionId == subscriptionId)
                .Select(d => d.EventId).ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            if (envelope is null || !envelope.IsValid)
                throw Error(EventingErrorCodes.EnvelopeInvalid, "The event envelope is invalid.");
            if (!existing.Add(envelope.EventId)) continue;
            db.EventDeliveries.Add(new EventDeliveryEntity
            {
                DeliveryId = "delivery-" + Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId,
                EventId = envelope.EventId,
                EventSequence = envelope.Sequence,
                EnvelopeJson = JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web),
                Attempt = 0,
                ClaimedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<EventDelivery>> ClaimAsync(string subscriptionId, int maxCount,
        string ownerId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128
            || maxCount is < 1 or > 256 || leaseUntil <= now)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded, owned lease request is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var nowMs = now.ToUnixTimeMilliseconds();
        var rows = await db.EventDeliveries
            .Where(d => d.SubscriptionId == subscriptionId && !d.DeadLettered
                        && (d.LeaseOwnerId == null || d.LeaseUntilUnixMs == null || d.LeaseUntilUnixMs <= nowMs
                            || d.LeaseOwnerId == ownerId))
            .OrderBy(d => d.EventSequence).Take(maxCount).ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.LeaseOwnerId = ownerId;
            row.LeaseUntilUnixMs = leaseUntil.ToUnixTimeMilliseconds();
            row.ClaimedAt = now;
            row.Attempt++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToDelivery)];
    }

    public async ValueTask<ImmutableArray<EventDelivery>> ListPendingAsync(string subscriptionId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.EventDeliveries.AsNoTracking()
            .Where(d => d.SubscriptionId == subscriptionId && !d.DeadLettered)
            .OrderBy(d => d.EventSequence).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToDelivery)];
    }

    public async ValueTask FailAsync(string subscriptionId, string deliveryId, string ownerId, string failureCode,
        bool deadLetter, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 128)
            throw Error(EventingErrorCodes.RequestInvalid, "A bounded failure code is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await FindAsync(db, subscriptionId, deliveryId, ct).ConfigureAwait(false);
        if (!string.Equals(row.LeaseOwnerId, ownerId, StringComparison.Ordinal))
            throw Error(EventingErrorCodes.LeaseStale, "Only the current lease owner can fail this delivery.");
        row.FailureCode = failureCode;
        row.ClaimedAt = now;
        row.Attempt++;
        row.DeadLettered = deadLetter;
        row.LeaseOwnerId = null;
        row.LeaseUntilUnixMs = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<EventDelivery>> ListDeadLettersAsync(string subscriptionId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.EventDeliveries.AsNoTracking()
            .Where(d => d.SubscriptionId == subscriptionId && d.DeadLettered)
            .OrderBy(d => d.EventSequence).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToDelivery)];
    }

    public async ValueTask<EventDelivery> RequeueDeadLetterAsync(string subscriptionId, string deliveryId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await FindAsync(db, subscriptionId, deliveryId, ct).ConfigureAwait(false);
        if (!row.DeadLettered)
            throw Error(EventingErrorCodes.DeadLetterMissing, "The delivery is not a dead letter.");
        row.DeadLettered = false;
        row.Attempt++;
        row.ClaimedAt = DateTimeOffset.UtcNow;
        row.LeaseOwnerId = null;
        row.LeaseUntilUnixMs = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDelivery(row);
    }

    private static async Task<EventDeliveryEntity> FindAsync(PlatformDbContext db, string subscriptionId,
        string deliveryId, CancellationToken ct)
        => await db.EventDeliveries.SingleOrDefaultAsync(d => d.DeliveryId == deliveryId
                && d.SubscriptionId == subscriptionId, ct).ConfigureAwait(false)
            ?? throw Error(EventingErrorCodes.RequestInvalid, "The delivery does not belong to this subscription.");

    private static EventDelivery ToDelivery(EventDeliveryEntity row)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(row.EnvelopeJson, JsonSerializerOptions.Web)
            ?? throw Error(EventingErrorCodes.EnvelopeInvalid, "A stored envelope is unreadable.");
        return new(row.DeliveryId, row.SubscriptionId, envelope, row.EventSequence, row.Attempt, row.ClaimedAt,
            row.FailureCode, row.LeaseOwnerId,
            row.LeaseUntilUnixMs is { } until ? DateTimeOffset.FromUnixTimeMilliseconds(until) : null);
    }

    private static EventingException Error(string code, string message) => new(code, message);
}
