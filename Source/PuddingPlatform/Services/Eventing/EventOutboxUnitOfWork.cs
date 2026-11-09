using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Eventing;

/// <summary>
/// PA-07A S5 pending outbox fact. It has no committed sequence yet; the UoW assigns one at commit, so a fact cannot
/// pretend to be a committed <see cref="EventEnvelope"/>.
/// </summary>
public sealed record OutboxFact(string EventId, string StreamId, string Kind, int SchemaVersion,
    string WorkspaceId, string PayloadRef, string PayloadHash, string Producer, DateTimeOffset OccurredAt,
    string? CausationId = null, string? CorrelationId = null, string? BusinessRef = null)
{
    public bool IsValid => EventEnvelope.Valid(EventId) && EventEnvelope.Valid(StreamId) && EventEnvelope.Valid(Kind)
        && SchemaVersion >= 1 && EventEnvelope.Valid(WorkspaceId) && EventEnvelope.Valid(PayloadRef)
        && EventEnvelope.Valid(PayloadHash) && EventEnvelope.Valid(Producer) && OccurredAt != default;
}

/// <summary>Append-only view handed to UoW participants; they cannot begin/commit/rollback.</summary>
public interface IOutboxAppendView
{
    ValueTask AppendAsync(OutboxFact fact, CancellationToken ct = default);
}

/// <summary>
/// Platform-internal unit of work: the callback receives the already-open business DbContext and the append-only
/// outbox view. The UoW alone commits/rolls back, so business state and outbox facts share one database transaction.
/// </summary>
public interface IOutboxUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<PlatformDbContext, IOutboxAppendView, CancellationToken, Task<T>> work,
        CancellationToken ct = default);
}

public sealed class EventOutboxUnitOfWork(IDbContextFactory<PlatformDbContext> dbFactory) : IOutboxUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<PlatformDbContext, IOutboxAppendView, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var view = new AppendView();
        try
        {
            var result = await work(db, view, ct).ConfigureAwait(false);
            var duplicates = await db.EventOutbox.Where(e => view.Facts.Select(f => f.EventId).Contains(e.EventId))
                .Select(e => e.EventId).ToListAsync(ct).ConfigureAwait(false);
            if (duplicates.Count != 0)
                throw new EventingException(EventingErrorCodes.DuplicateEvent,
                    "An outbox fact with the same event id was already committed.");
            foreach (var group in view.Facts.GroupBy(f => f.StreamId, StringComparer.Ordinal))
            {
                var sequence = await db.EventOutbox.Where(e => e.StreamId == group.Key)
                    .Select(e => (long?)e.Sequence).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                foreach (var fact in group)
                {
                    sequence++;
                    db.EventOutbox.Add(new OutboxEventEntity
                    {
                        EventId = fact.EventId,
                        StreamId = fact.StreamId,
                        Sequence = sequence,
                        Kind = fact.Kind,
                        SchemaVersion = fact.SchemaVersion,
                        OccurredAt = fact.OccurredAt,
                        OccurredAtUnixMs = fact.OccurredAt.ToUnixTimeMilliseconds(),
                        WorkspaceId = fact.WorkspaceId,
                        PayloadRef = fact.PayloadRef,
                        PayloadHash = fact.PayloadHash,
                        Producer = fact.Producer,
                        CausationId = fact.CausationId,
                        CorrelationId = fact.CorrelationId,
                        BusinessRef = fact.BusinessRef,
                    });
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class AppendView : IOutboxAppendView
    {
        public List<OutboxFact> Facts { get; } = [];

        public ValueTask AppendAsync(OutboxFact fact, CancellationToken ct = default)
        {
            if (fact is null || !fact.IsValid)
                throw new EventingException(EventingErrorCodes.EnvelopeInvalid, "The outbox fact is invalid.");
            Facts.Add(fact);
            return ValueTask.CompletedTask;
        }
    }
}
