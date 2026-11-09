using System.Collections.Immutable;
using PuddingEventing.Contracts;

namespace PuddingEventing;

/// <summary>
/// PA-07A delivery driver: authorization filter, at-least-once claiming, durable checkpoint only after the claimed
/// batch prefix is acknowledged, dead letters and bounded batches (backpressure).
///
/// Acknowledgement continuity is over the claimed delivery batch, not over raw EventSequence numbers: an event of a
/// non-subscribed kind inside the retained range may be skipped, but a claimed delivery may never be skipped. A page
/// whose events are entirely filtered advances only the internal scan position (never the public cursor) once no
/// pending or dead-lettered delivery blocks it, so repeated reads do not spin on the same page.
/// </summary>
public sealed class SubscriptionDispatcher
{
    private readonly ICommittedEventLog _log;
    private readonly ISubscriptionStore _subscriptions;
    private readonly IEventDeliveryStore _deliveries;
    private readonly IEventCheckpointStore _checkpoint;
    private readonly TimeProvider _clock;
    public const int MaxBatch = 64;

    public SubscriptionDispatcher(ICommittedEventLog log, ISubscriptionStore subscriptions,
        IEventDeliveryStore deliveries, IEventCheckpointStore checkpoint, TimeProvider? clock = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        _clock = clock ?? TimeProvider.System;
    }

    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    public async ValueTask<EventDispatchResult> DispatchAsync(string subscriptionId, string ownerId, int maxDeliveries,
        TimeSpan? lease = null, CancellationToken ct = default)
    {
        if (maxDeliveries is < 1 or > MaxBatch) throw new EventingException(EventingErrorCodes.RequestInvalid, "The delivery batch is out of bounds.");
        EnsureOwner(ownerId);
        var leaseDuration = lease ?? DefaultLease;
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new EventingException(EventingErrorCodes.RequestInvalid, "The delivery lease is out of bounds.");
        var subscription = await LoadLiveAsync(subscriptionId, ct).ConfigureAwait(false);
        var after = Math.Max(subscription.CursorSequence, subscription.ScannedThroughSequence);
        var page = await _log.ReadAsync(new(subscription.Grant.WorkspaceId, after, Math.Max(MaxBatch, maxDeliveries)), ct)
            .ConfigureAwait(false);
        // Retention: AfterSequence + 1 < OldestAvailableSequence, written overflow-safe (Oldest >= 1).
        if (page.SnapshotRequired || (page.OldestAvailableSequence > 0 && after < page.OldestAvailableSequence - 1))
            return new EventDispatchResult(ImmutableArray<EventDelivery>.Empty, SnapshotRequired: true, page.OldestAvailableSequence);
        // Only authorized kinds in the bound workspace and events strictly after the acknowledged cursor are deliverable.
        var events = page.Events
            .Where(e => e.IsValid && e.Sequence > subscription.CursorSequence
                        && string.Equals(e.WorkspaceId, subscription.Grant.WorkspaceId, StringComparison.Ordinal)
                        && subscription.Grant.Kinds.Contains(e.Kind))
            .OrderBy(e => e.Sequence)
            .Take(maxDeliveries)
            .ToImmutableArray();
        if (!events.IsEmpty)
            await _deliveries.EnqueueAsync(subscriptionId, events, ct).ConfigureAwait(false);
        // Claim only what this owner may own: unleased, expired, or its own lease. A live lease owned by another
        // worker is never handed out again.
        var now = _clock.GetUtcNow();
        var claimed = await _deliveries.ClaimAsync(subscriptionId, maxDeliveries, ownerId, now, now.Add(leaseDuration), ct)
            .ConfigureAwait(false);
        if (!claimed.IsDefaultOrEmpty)
            return new EventDispatchResult(claimed, false, page.OldestAvailableSequence);
        if (!events.IsEmpty)
            return new EventDispatchResult(ImmutableArray<EventDelivery>.Empty, false, page.OldestAvailableSequence);
        var scannedThrough = page.Events.IsEmpty ? after : page.Events[^1].Sequence;
        if (scannedThrough > subscription.ScannedThroughSequence)
        {
            try
            {
                await _checkpoint.AdvanceScanAsync(subscriptionId, scannedThrough, subscription.Revision, ct)
                    .ConfigureAwait(false);
            }
            catch (EventingException ex) when (ex.Code is EventingErrorCodes.CursorConflict
                                                   or EventingErrorCodes.CursorGap
                                                   or EventingErrorCodes.DeadLetterBlocked)
            {
                // A concurrent acknowledgement or a dead letter blocks the advance; the caller re-reads later.
            }
        }
        return new EventDispatchResult(ImmutableArray<EventDelivery>.Empty, false, page.OldestAvailableSequence);
    }

    /// <summary>
    /// Acknowledges an ascending prefix of the claimed batch and only then advances the durable cursor. The prefix
    /// check is over delivery order, not over <c>Sequence + 1</c>: filtered-out events may sit between two claimed
    /// events. A stale owner/checkpoint revision rejects the whole batch; a dead letter blocks the cursor.
    /// </summary>
    public async ValueTask<long> AckAsync(string subscriptionId, string ownerId, ImmutableArray<EventDelivery> claimed,
        ImmutableArray<string> ackedDeliveryIds, CancellationToken ct = default)
    {
        EnsureOwner(ownerId);
        if (claimed.IsDefaultOrEmpty || ackedDeliveryIds.IsDefaultOrEmpty)
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A claimed batch and at least one delivery id are required.");
        if (ackedDeliveryIds.Distinct(StringComparer.Ordinal).Count() != ackedDeliveryIds.Length)
            throw new EventingException(EventingErrorCodes.RequestInvalid, "Duplicate acknowledgement ids are not allowed.");
        var subscription = await LoadLiveAsync(subscriptionId, ct).ConfigureAwait(false);
        var ordered = claimed.OrderBy(d => d.EventSequence).ThenBy(d => d.DeliveryId, StringComparer.Ordinal).ToArray();
        var ackedSet = ackedDeliveryIds.ToHashSet(StringComparer.Ordinal);
        var acknowledged = ordered.Where(d => ackedSet.Contains(d.DeliveryId)).ToArray();
        if (acknowledged.Length != ackedDeliveryIds.Length)
            throw new EventingException(EventingErrorCodes.GrantDenied, "The delivery does not belong to this subscription batch.");
        // The acknowledged deliveries must exactly be a prefix of the claimed batch.
        var prefix = ordered.Take(acknowledged.Length).Select(d => d.DeliveryId).ToArray();
        if (!prefix.SequenceEqual(acknowledged.Select(d => d.DeliveryId), StringComparer.Ordinal))
            throw new EventingException(EventingErrorCodes.CursorGap, "Acknowledgements must form the claimed batch prefix.");
        var nextSequence = acknowledged[^1].EventSequence;
        if (nextSequence <= subscription.CursorSequence)
            throw new EventingException(EventingErrorCodes.CursorGap, "The acknowledged events were already consumed.");
        // Removing the deliveries and advancing the cursor is one transaction owned by the checkpoint store.
        var updated = await _checkpoint.AckAndAdvanceAsync(subscriptionId, ownerId, ackedDeliveryIds, nextSequence,
            subscription.Revision, ct).ConfigureAwait(false);
        return updated.CursorSequence;
    }

    public async ValueTask FailAsync(string subscriptionId, string ownerId, string deliveryId, string failureCode,
        bool deadLetter, CancellationToken ct = default)
    {
        EnsureOwner(ownerId);
        if (!EventEnvelope.Valid(failureCode))
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A typed failure code is required.");
        await LoadLiveAsync(subscriptionId, ct).ConfigureAwait(false);
        await _deliveries.FailAsync(subscriptionId, deliveryId, ownerId, failureCode, deadLetter, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<EventDelivery>> DeadLettersAsync(string subscriptionId, CancellationToken ct = default)
    {
        await LoadLiveAsync(subscriptionId, ct).ConfigureAwait(false);
        return await _deliveries.ListDeadLettersAsync(subscriptionId, ct).ConfigureAwait(false);
    }

    /// <summary>Replay is an explicit, authorized host action; it never silently re-enables a cancelled subscription.</summary>
    public async ValueTask<EventDelivery> ReplayDeadLetterAsync(string subscriptionId, string deliveryId, CancellationToken ct = default)
    {
        await LoadLiveAsync(subscriptionId, ct).ConfigureAwait(false);
        return await _deliveries.RequeueDeadLetterAsync(subscriptionId, deliveryId, ct).ConfigureAwait(false);
    }

    private static void EnsureOwner(string? ownerId)
    {
        if (!EventEnvelope.Valid(ownerId))
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A bounded lease owner is required.");
    }

    private async ValueTask<SubscriptionRecord> LoadLiveAsync(string subscriptionId, CancellationToken ct)
    {
        if (!EventEnvelope.Valid(subscriptionId))
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A subscription id is required.");
        var subscription = await _subscriptions.GetAsync(subscriptionId, ct).ConfigureAwait(false);
        if (subscription is not { IsValid: true })
            throw new EventingException(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");
        if (subscription.Cancelled)
            throw new EventingException(EventingErrorCodes.SubscriptionCancelled, "The subscription was cancelled.");
        return subscription;
    }
}

public sealed record EventDispatchResult(ImmutableArray<EventDelivery> Deliveries, bool SnapshotRequired, long OldestAvailableSequence);
