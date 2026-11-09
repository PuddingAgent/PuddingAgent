using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// PA-07A S5 committed event log over the Platform outbox: only facts committed through the UoW are visible; the
/// workspace is the distribution stream, and the retained floor is the smallest committed stream sequence.
/// </summary>
public sealed class OutboxCommittedEventLog(IDbContextFactory<PlatformDbContext> dbFactory) : ICommittedEventLog
{
    public async ValueTask<EventPage> ReadAsync(EventReadRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.StreamId) || request.StreamId.Length > 128 || request.AfterSequence < 0
            || request.MaxCount is < 1 or > 256)
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A bounded event read is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.EventOutbox.AsNoTracking().Where(e => e.StreamId == request.StreamId);
        var oldest = await query.Select(e => (long?)e.Sequence).MinAsync(ct).ConfigureAwait(false) ?? 0;
        if (oldest > 0 && request.AfterSequence < oldest - 1)
            return new EventPage(ImmutableArray<EventEnvelope>.Empty, request.AfterSequence, oldest, SnapshotRequired: true);
        var rows = await query.Where(e => e.Sequence > request.AfterSequence)
            .OrderBy(e => e.Sequence).Take(request.MaxCount).ToListAsync(ct).ConfigureAwait(false);
        var events = rows.Select(ToEnvelope).ToImmutableArray();
        var next = events.IsEmpty ? request.AfterSequence : events[^1].Sequence;
        return new EventPage(events, next, oldest, SnapshotRequired: false);
    }

    private static EventEnvelope ToEnvelope(OutboxEventEntity e) => new(e.EventId, e.StreamId, e.Sequence, e.Kind,
        e.SchemaVersion, e.OccurredAt, e.WorkspaceId, e.PayloadRef, e.PayloadHash, e.Producer, e.CausationId,
        e.CorrelationId, e.BusinessRef);
}
