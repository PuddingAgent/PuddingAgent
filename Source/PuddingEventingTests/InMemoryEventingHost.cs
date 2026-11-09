using System.Collections.Immutable;
using PuddingEventing.Contracts;

namespace PuddingEventingTests;

/// <summary>
/// Independent in-memory adapter for every PA-07A port, with one lock so outbox commit, lease claiming and
/// checkpoint advance are atomic like the future persistent store. Not a product implementation.
/// </summary>
public sealed class InMemoryEventingHost : ICommittedEventLog, ISubscriptionStore, IEventDeliveryStore,
    IEventCheckpointStore, IPluginJobStore, IEventOutboxWriter
{
    private readonly object _gate = new();
    private readonly List<EventEnvelope> _events = [];
    private readonly Dictionary<string, long> _oldestAvailable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscriptionRecord> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeliveryState> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventDelivery> _deadLetters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PluginJobRecord Record, PluginJobSnapshot Snapshot)> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _jobByOperation = new(StringComparer.Ordinal);
    private long _jobSequence;

    public EventEnvelope AppendEvent(string streamId, string kind, string workspaceId, string? payloadRef = null,
        string? businessRef = null)
    {
        lock (_gate)
        {
            var sequence = _nextSequence.GetValueOrDefault(streamId) + 1;
            _nextSequence[streamId] = sequence;
            var envelope = new EventEnvelope(Guid.NewGuid().ToString("N"), streamId, sequence, kind, 1,
                DateTimeOffset.UtcNow, workspaceId, payloadRef ?? $"payload-{sequence}", "sha256-payload",
                "canonical", BusinessRef: businessRef);
            _events.Add(envelope);
            return envelope;
        }
    }

    public void SetOldestAvailable(string streamId, long sequence)
    {
        lock (_gate) _oldestAvailable[streamId] = sequence;
    }

    /// <summary>Simulates another writer advancing the checkpoint, to prove a stale acknowledgement cannot win.</summary>
    public void AdvanceCursorForTest(string subscriptionId, long sequence)
    {
        lock (_gate)
        {
            var record = _subscriptions[subscriptionId];
            _subscriptions[subscriptionId] = record with
            {
                CursorSequence = sequence,
                ScannedThroughSequence = Math.Max(record.ScannedThroughSequence, sequence),
                Revision = record.Revision + 1,
            };
        }
    }

    /// <summary>Sets both checkpoint positions for retention/scan boundary tests.</summary>
    public void SetCheckpointForTest(string subscriptionId, long cursorSequence, long scannedThroughSequence)
    {
        lock (_gate)
        {
            var record = _subscriptions[subscriptionId];
            _subscriptions[subscriptionId] = record with
            {
                CursorSequence = cursorSequence,
                ScannedThroughSequence = scannedThroughSequence,
                Revision = record.Revision + 1,
            };
        }
    }
    public int PendingCount(string subscriptionId)
    {
        lock (_gate) return _deliveries.TryGetValue(subscriptionId, out var state) ? state.Pending.Count : 0;
    }

    public int DeadLetterCount(string subscriptionId)
    {
        lock (_gate) return _deadLetters.Values.Count(d => d.SubscriptionId == subscriptionId);
    }

    public ValueTask<EventPage> ReadAsync(EventReadRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var oldest = _oldestAvailable.GetValueOrDefault(request.StreamId, 1);
            // Overflow-safe equivalent of (AfterSequence + 1 < OldestAvailableSequence) for oldest >= 1.
            if (oldest > 0 && request.AfterSequence < oldest - 1)
                return ValueTask.FromResult(new EventPage([], request.AfterSequence, oldest, SnapshotRequired: true));
            var events = _events.Where(e => e.StreamId == request.StreamId && e.Sequence > request.AfterSequence)
                .OrderBy(e => e.Sequence).Take(request.MaxCount).ToImmutableArray();
            return ValueTask.FromResult(new EventPage(events, events.IsEmpty ? request.AfterSequence : events[^1].Sequence,
                oldest, false));
        }
    }

    public ValueTask<SubscriptionRecord> CreateAsync(SubscriptionGrant grant, CancellationToken ct = default)
    {
        if (grant is null || !grant.IsValid) throw new EventingException(EventingErrorCodes.GrantMissing, "A valid host-bound grant is required.");
        lock (_gate)
        {
            var record = new SubscriptionRecord(Guid.NewGuid().ToString("N"), grant, 0, 0, DateTimeOffset.UtcNow, false);
            _subscriptions[record.SubscriptionId] = record;
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<SubscriptionRecord> GetAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var record))
                throw new EventingException(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask CancelAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var record = _subscriptions[subscriptionId];
            _subscriptions[subscriptionId] = record with { Cancelled = true, Revision = record.Revision + 1 };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<ImmutableArray<SubscriptionRecord>> ListAsync(string workspaceId, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_subscriptions.Values
                .Where(r => string.Equals(r.Grant.WorkspaceId, workspaceId, StringComparison.Ordinal))
                .OrderBy(r => r.CreatedAt).ToImmutableArray());
    }

    public ValueTask EnqueueAsync(string subscriptionId, ImmutableArray<EventEnvelope> events,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = State(subscriptionId);
            foreach (var envelope in events)
            {
                if (!state.DeliveredEventIds.Add(envelope.EventId)) continue;
                var delivery = new EventDelivery(Guid.NewGuid().ToString("N"), subscriptionId, envelope,
                    envelope.Sequence, 0, DateTimeOffset.UtcNow, null);
                state.Pending[delivery.DeliveryId] = delivery;
            }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<ImmutableArray<EventDelivery>> ClaimAsync(string subscriptionId, int maxCount, string ownerId,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = State(subscriptionId);
            var claimed = state.Pending.Values
                .Where(d => d.LeaseOwner is null || d.LeaseOwner == ownerId || d.LeaseUntil is null || d.LeaseUntil <= now)
                .OrderBy(d => d.EventSequence).Take(maxCount)
                .Select(d => d with { Attempt = d.Attempt + 1, ClaimedAt = now, LeaseOwner = ownerId, LeaseUntil = leaseUntil })
                .ToArray();
            foreach (var delivery in claimed) state.Pending[delivery.DeliveryId] = delivery;
            return ValueTask.FromResult(claimed.ToImmutableArray());
        }
    }

    public ValueTask<ImmutableArray<EventDelivery>> ListPendingAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(Pending(subscriptionId).OrderBy(d => d.EventSequence).ToImmutableArray());
    }

    public ValueTask FailAsync(string subscriptionId, string deliveryId, string ownerId, string failureCode,
        bool deadLetter, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = State(subscriptionId);
            if (!state.Pending.TryGetValue(deliveryId, out var delivery))
                throw new EventingException(EventingErrorCodes.RequestInvalid, "The delivery is not pending.");
            if (!string.Equals(delivery.LeaseOwner, ownerId, StringComparison.Ordinal))
                throw new EventingException(EventingErrorCodes.LeaseStale, "Only the current lease owner can fail this delivery.");
            if (deadLetter)
            {
                state.Pending.Remove(deliveryId);
                _deadLetters[deliveryId] = delivery with
                {
                    FailureCode = failureCode,
                    ClaimedAt = now,
                    Attempt = delivery.Attempt + 1,
                    LeaseOwner = null,
                    LeaseUntil = null,
                };
            }
            else
            {
                state.Pending[deliveryId] = delivery with { FailureCode = failureCode, Attempt = delivery.Attempt + 1, ClaimedAt = now };
            }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<ImmutableArray<EventDelivery>> ListDeadLettersAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_deadLetters.Values.Where(d => d.SubscriptionId == subscriptionId)
                .OrderBy(d => d.EventSequence).ToImmutableArray());
    }

    public ValueTask<EventDelivery> RequeueDeadLetterAsync(string subscriptionId, string deliveryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_deadLetters.Remove(deliveryId, out var delivery) || delivery.SubscriptionId != subscriptionId)
                throw new EventingException(EventingErrorCodes.DeadLetterMissing, "The dead letter is missing.");
            var requeued = delivery with
            {
                Attempt = delivery.Attempt + 1,
                FailureCode = null,
                ClaimedAt = DateTimeOffset.UtcNow,
                LeaseOwner = null,
                LeaseUntil = null,
            };
            State(subscriptionId).Pending[deliveryId] = requeued;
            return ValueTask.FromResult(requeued);
        }
    }

    public ValueTask<SubscriptionRecord> AckAndAdvanceAsync(string subscriptionId, string ownerId,
        ImmutableArray<string> deliveryIds, long nextSequence, long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var record))
                throw new EventingException(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");
            if (record.Cancelled) throw new EventingException(EventingErrorCodes.SubscriptionCancelled, "The subscription was cancelled.");
            if (record.Revision != expectedRevision)
                throw new EventingException(EventingErrorCodes.CursorConflict, "The checkpoint revision changed; the batch is rejected as a whole.");
            var state = State(subscriptionId);
            if (deliveryIds.Any(id => !state.Pending.ContainsKey(id)))
                throw new EventingException(EventingErrorCodes.RequestInvalid, "The acknowledgement contains an unknown delivery.");
            if (deliveryIds.Any(id => !string.Equals(state.Pending[id].LeaseOwner, ownerId, StringComparison.Ordinal)))
                throw new EventingException(EventingErrorCodes.LeaseStale, "The acknowledgement owner does not hold the lease.");
            var blocked = _deadLetters.Values
                .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal)
                            && d.EventSequence <= nextSequence)
                .Select(d => d.EventSequence).DefaultIfEmpty(long.MaxValue).Min();
            if (blocked <= nextSequence)
                throw new EventingException(EventingErrorCodes.DeadLetterBlocked,
                    "A dead-lettered delivery still blocks the acknowledgement cursor.");
            foreach (var id in deliveryIds) state.Pending.Remove(id);
            var updated = record with
            {
                CursorSequence = nextSequence,
                ScannedThroughSequence = Math.Max(record.ScannedThroughSequence, nextSequence),
                Revision = record.Revision + 1,
            };
            _subscriptions[subscriptionId] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<SubscriptionRecord> AdvanceScanAsync(string subscriptionId, long scannedThroughSequence,
        long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var record))
                throw new EventingException(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");
            if (record.Cancelled) throw new EventingException(EventingErrorCodes.SubscriptionCancelled, "The subscription was cancelled.");
            if (record.Revision != expectedRevision)
                throw new EventingException(EventingErrorCodes.CursorConflict, "The checkpoint revision changed.");
            if (scannedThroughSequence <= record.ScannedThroughSequence || scannedThroughSequence < record.CursorSequence)
                throw new EventingException(EventingErrorCodes.CursorGap, "The scan position must advance monotonically.");
            if (State(subscriptionId).Pending.Count != 0)
                throw new EventingException(EventingErrorCodes.CursorGap, "A pending delivery blocks scan progress.");
            var blocked = _deadLetters.Values
                .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal)
                            && d.EventSequence <= scannedThroughSequence)
                .Select(d => d.EventSequence).DefaultIfEmpty(long.MaxValue).Min();
            if (blocked <= scannedThroughSequence)
                throw new EventingException(EventingErrorCodes.DeadLetterBlocked, "A dead letter blocks scan progress.");
            var updated = record with { ScannedThroughSequence = scannedThroughSequence, Revision = record.Revision + 1 };
            _subscriptions[subscriptionId] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<PluginJobTicket> SubmitAsync(PluginJobRecord record, CancellationToken ct = default)
    {
        if (record is null || !record.IsValid) throw new JobException(JobErrorCodes.RequestInvalid, "A valid job record is required.");
        lock (_gate)
        {
            var key = record.Binding.WorkspaceId + "|" + record.Request.OperationId;
            if (_jobByOperation.TryGetValue(key, out var existingId))
            {
                var existing = _jobs[existingId];
                if (!SameContent(existing.Record, record))
                    throw new JobException(JobErrorCodes.Conflict, "The operation id was already used with different job content.");
                return ValueTask.FromResult(new PluginJobTicket(existing.Record.JobId, existing.Record.AcceptedSequence));
            }
            _jobSequence++;
            var accepted = record with { AcceptedSequence = _jobSequence };
            var snapshot = new PluginJobSnapshot(accepted.JobId, PluginJobState.Accepted, accepted.Binding.PluginId,
                accepted.Binding.WorkspaceId, accepted.Binding.PluginGeneration, null, null, null, accepted.AcceptedAt, 0);
            _jobs[accepted.JobId] = (accepted, snapshot);
            _jobByOperation[key] = accepted.JobId;
            return ValueTask.FromResult(new PluginJobTicket(accepted.JobId, accepted.AcceptedSequence));
        }
    }

    ValueTask<PluginJobSnapshot> IPluginJobStore.GetAsync(string jobId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
                throw new JobException(JobErrorCodes.JobMissing, "The job is missing.");
            return ValueTask.FromResult(job.Snapshot);
        }
    }

    public ValueTask<PluginJobSnapshot> CancelAsync(string jobId, long pluginGeneration, string reasonCode,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
                throw new JobException(JobErrorCodes.JobMissing, "The job is missing.");
            if (job.Snapshot.PluginGeneration != pluginGeneration)
                throw new JobException(JobErrorCodes.GenerationStale, "A stale generation cannot cancel this job.");
            if (job.Snapshot.State is PluginJobState.Succeeded or PluginJobState.Failed or PluginJobState.Cancelled)
                return ValueTask.FromResult(job.Snapshot);
            var cancelled = job.Snapshot with
            {
                State = PluginJobState.Cancelled,
                ReasonCode = reasonCode,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _jobs[jobId] = (job.Record, cancelled);
            return ValueTask.FromResult(cancelled);
        }
    }

    ValueTask<ImmutableArray<PluginJobSnapshot>> IPluginJobStore.ListAsync(string workspaceId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(_jobs.Values.Select(j => j.Snapshot)
                .Where(s => string.Equals(s.WorkspaceId, workspaceId, StringComparison.Ordinal))
                .OrderBy(s => s.JobId).ToImmutableArray());
    }

    public ValueTask<PluginJobSnapshot> TransitionAsync(string jobId, long pluginGeneration, PluginJobState state,
        string? reasonCode, string? resultRef, string? resultHash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
                throw new JobException(JobErrorCodes.JobMissing, "The job is missing.");
            if (job.Snapshot.PluginGeneration != pluginGeneration)
                throw new JobException(JobErrorCodes.GenerationStale, "A stale generation cannot commit a job result.");
            if (job.Snapshot.State is PluginJobState.Succeeded or PluginJobState.Failed or PluginJobState.Cancelled)
                throw new JobException(JobErrorCodes.StateInvalid, "A terminal job cannot transition again.");
            if (state is PluginJobState.Accepted) throw new JobException(JobErrorCodes.StateInvalid, "A job cannot return to Accepted.");
            var updated = job.Snapshot with
            {
                State = state,
                ReasonCode = reasonCode,
                ResultRef = resultRef,
                ResultHash = resultHash,
                Attempt = job.Snapshot.Attempt + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _jobs[jobId] = (job.Record, updated);
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<IEventOutboxTransaction> BeginAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IEventOutboxTransaction>(new OutboxTransaction(this));

    private void CommitOutbox(List<EventEnvelope> appended)
    {
        lock (_gate)
        {
            var existing = _events.Select(e => e.EventId).Concat(appended.Select(e => e.EventId)).ToArray();
            if (existing.Distinct(StringComparer.Ordinal).Count() != existing.Length)
                throw new EventingException(EventingErrorCodes.DuplicateEvent, "An event id was already committed.");
            foreach (var envelope in appended)
            {
                var sequence = _nextSequence.GetValueOrDefault(envelope.StreamId) + 1;
                _nextSequence[envelope.StreamId] = sequence;
                _events.Add(envelope with { Sequence = sequence });
            }
        }
    }

    private static bool SameContent(PluginJobRecord existing, PluginJobRecord incoming)
        => existing.Request.Kind == incoming.Request.Kind
           && existing.Request.InputRef == incoming.Request.InputRef
           && existing.Request.InputHash == incoming.Request.InputHash
           && existing.Request.RequiredGrants.SequenceEqual(incoming.Request.RequiredGrants);

    private DeliveryState State(string subscriptionId)
    {
        if (!_deliveries.TryGetValue(subscriptionId, out var state))
        {
            state = new DeliveryState();
            _deliveries[subscriptionId] = state;
        }
        return state;
    }

    private IEnumerable<EventDelivery> Pending(string subscriptionId)
        => _deliveries.TryGetValue(subscriptionId, out var state) ? state.Pending.Values : [];

    private sealed class DeliveryState
    {
        public readonly HashSet<string> DeliveredEventIds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EventDelivery> Pending = new(StringComparer.Ordinal);
    }

    private sealed class OutboxTransaction(InMemoryEventingHost host) : IEventOutboxTransaction
    {
        private readonly List<EventEnvelope> _appended = [];
        private bool _committed, _disposed;
        public ValueTask AppendAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (_committed || _disposed) throw new EventingException(EventingErrorCodes.RequestInvalid, "The transaction is closed.");
            if (envelope is null || !EventEnvelope.Valid(envelope.EventId) || !EventEnvelope.Valid(envelope.StreamId)
                || !EventEnvelope.Valid(envelope.Kind) || envelope.SchemaVersion < 1 || !EventEnvelope.Valid(envelope.WorkspaceId))
                throw new EventingException(EventingErrorCodes.EnvelopeInvalid, "The outbox envelope is invalid.");
            _appended.Add(envelope);
            return ValueTask.CompletedTask;
        }
        public ValueTask CommitAsync(CancellationToken ct = default)
        {
            if (_committed || _disposed) throw new EventingException(EventingErrorCodes.RequestInvalid, "The transaction is closed.");
            host.CommitOutbox(_appended);
            _committed = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
