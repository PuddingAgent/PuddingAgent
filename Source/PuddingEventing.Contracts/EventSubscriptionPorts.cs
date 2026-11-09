using System.Collections.Immutable;

namespace PuddingEventing.Contracts;

// PA-07A：可靠订阅端口。持久业务事实与 outbox 原子提交，投递至少一次；
// 消费者以 EventId/业务操作 ID 幂等；处理成功才推进 checkpoint；游标与 grant 由宿主绑定。

public sealed record EventEnvelope(string EventId, string StreamId, long Sequence, string Kind, int SchemaVersion,
    DateTimeOffset OccurredAt, string WorkspaceId, string PayloadRef, string PayloadHash, string Producer,
    string? CausationId = null, string? CorrelationId = null, string? BusinessRef = null)
{
    public bool IsValid => Valid(EventId) && Valid(StreamId) && Sequence > 0 && Valid(Kind) && SchemaVersion >= 1
        && OccurredAt != default && Valid(WorkspaceId) && Valid(PayloadRef) && Valid(PayloadHash) && Valid(Producer)
        && (CausationId is null || CausationId.Length <= 128) && (CorrelationId is null || CorrelationId.Length <= 128)
        && (BusinessRef is null || BusinessRef.Length <= 256);

    public static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record EventCursor(string StreamId, long Sequence);

public sealed record EventReadRequest(string StreamId, long AfterSequence, int MaxCount);

/// <summary>Committed canonical events only. SnapshotRequired is set when the requested cursor predates retention; skipping silently is forbidden.</summary>
public sealed record EventPage(ImmutableArray<EventEnvelope> Events, long NextSequence, long OldestAvailableSequence, bool SnapshotRequired);

public interface ICommittedEventLog
{
    ValueTask<EventPage> ReadAsync(EventReadRequest request, CancellationToken ct = default);
}

/// <summary>Host-bound authorization: the caller never supplies its own plugin id, generation, workspace or kind set.</summary>
public sealed record SubscriptionGrant(string PluginId, long Generation, string WorkspaceId, ImmutableArray<string> Kinds)
{
    public bool IsValid => EventEnvelope.Valid(PluginId) && Generation >= 1 && EventEnvelope.Valid(WorkspaceId)
        && !Kinds.IsDefaultOrEmpty && Kinds.All(EventEnvelope.Valid) && Kinds.Distinct(StringComparer.Ordinal).Count() == Kinds.Length;
}

public sealed record SubscriptionRecord(string SubscriptionId, SubscriptionGrant Grant, long CursorSequence, long Revision,
    DateTimeOffset CreatedAt, bool Cancelled, long ScannedThroughSequence = 0)
{
    public bool IsValid => EventEnvelope.Valid(SubscriptionId) && Grant is not null && Revision >= 0 && CursorSequence >= 0
        && ScannedThroughSequence >= CursorSequence;
}

public interface ISubscriptionStore
{
    ValueTask<SubscriptionRecord> CreateAsync(SubscriptionGrant grant, CancellationToken ct = default);
    ValueTask<SubscriptionRecord> GetAsync(string subscriptionId, CancellationToken ct = default);
    ValueTask CancelAsync(string subscriptionId, CancellationToken ct = default);
    ValueTask<ImmutableArray<SubscriptionRecord>> ListAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>
/// The only checkpoint commit: removing acknowledged deliveries and advancing the durable cursor must be one
/// transaction. A conflicting revision fails the whole batch, so a crash or a concurrent writer cannot lose an event.
/// </summary>
public interface IEventCheckpointStore
{
    ValueTask<SubscriptionRecord> AckAndAdvanceAsync(string subscriptionId, string ownerId,
        ImmutableArray<string> deliveryIds, long nextSequence, long expectedRevision, CancellationToken ct = default);
    /// <summary>
    /// Advances only the internal scan position for a fully examined page that contained no deliverable event (all
    /// kinds were filtered). It never advances CursorSequence, never skips a pending or dead-lettered delivery, and is
    /// revision-fenced like an acknowledgement.
    /// </summary>
    ValueTask<SubscriptionRecord> AdvanceScanAsync(string subscriptionId, long scannedThroughSequence,
        long expectedRevision, CancellationToken ct = default);
}

public enum DeliveryOutcome { Claimed, Acked, Failed, DeadLettered }

/// <summary>A leased delivery. EventId per subscription is the idempotency key; a second enqueue of the same event is not delivered twice.</summary>
public sealed record EventDelivery(string DeliveryId, string SubscriptionId, EventEnvelope Event, long EventSequence,
    int Attempt, DateTimeOffset ClaimedAt, string? FailureCode, string? LeaseOwner = null,
    DateTimeOffset? LeaseUntil = null);

public interface IEventDeliveryStore
{
    /// <summary>Atomically dedupes by EventId; it never leases. A second enqueue of the same event is not delivered twice.</summary>
    ValueTask EnqueueAsync(string subscriptionId, ImmutableArray<EventEnvelope> events, CancellationToken ct = default);
    /// <summary>
    /// Leases up to MaxCount unacknowledged deliveries with a persistent owner/lease. A delivery owned by another
    /// live lease is never returned; an expired lease, or the same owner, may be re-leased. The owner token is the CAS
    /// fence for later failure and acknowledgement.
    /// </summary>
    ValueTask<ImmutableArray<EventDelivery>> ClaimAsync(string subscriptionId, int maxCount, string ownerId,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default);
    /// <summary>Currently leased (unacknowledged) deliveries, used to validate acknowledgement batches.</summary>
    ValueTask<ImmutableArray<EventDelivery>> ListPendingAsync(string subscriptionId, CancellationToken ct = default);
    /// <summary>Owner-fenced failure/dead-letter transition; a stale owner is rejected.</summary>
    ValueTask FailAsync(string subscriptionId, string deliveryId, string ownerId, string failureCode, bool deadLetter,
        DateTimeOffset now, CancellationToken ct = default);
    ValueTask<ImmutableArray<EventDelivery>> ListDeadLettersAsync(string subscriptionId, CancellationToken ct = default);
    ValueTask<EventDelivery> RequeueDeadLetterAsync(string subscriptionId, string deliveryId, CancellationToken ct = default);
}

/// <summary>
/// Transactional outbox: a business fact and its events commit together. At-least-once delivery starts only
/// after a successful commit; a crash before commit leaves neither fact nor event, a crash after commit still
/// delivers the event (duplicate delivery is allowed, duplicate EventId is not).
/// </summary>
public interface IEventOutboxWriter
{
    ValueTask<IEventOutboxTransaction> BeginAsync(CancellationToken ct = default);
}

public interface IEventOutboxTransaction : IAsyncDisposable
{
    /// <summary>Queues an event on this transaction. The host assigns the stream sequence during commit.</summary>
    ValueTask AppendAsync(EventEnvelope envelope, CancellationToken ct = default);
    ValueTask CommitAsync(CancellationToken ct = default);
}

public static class EventingErrorCodes
{
    public const string RequestInvalid = "event.request_invalid";
    public const string EnvelopeInvalid = "event.envelope_invalid";
    public const string SubscriptionMissing = "event.subscription_missing";
    public const string SubscriptionCancelled = "event.subscription_cancelled";
    public const string GrantMissing = "event.grant_missing";
    public const string GrantDenied = "event.grant_denied";
    public const string SnapshotRequired = "event.snapshot_required";
    public const string CursorGap = "event.cursor_gap";
    public const string CursorConflict = "event.cursor_conflict";
    public const string GenerationStale = "event.generation_stale";
    public const string Backpressure = "event.backpressure";
    public const string DeadLetterMissing = "event.dead_letter_missing";
    public const string DeadLetterBlocked = "event.dead_letter_blocked";
    public const string DuplicateEvent = "event.duplicate";
    public const string LeaseStale = "event.lease_stale";
}

public sealed class EventingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
