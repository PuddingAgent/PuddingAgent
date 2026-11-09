using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingEventing.Contracts;

namespace PuddingEventing;

/// <summary>
/// Host-bound plugin identity for one call. The plugin never supplies its own id, generation, workspace,
/// owner work, root budget or grant; everything comes from this binding.
/// </summary>
public sealed record HostPluginBinding(string PluginId, long Generation, string WorkspaceId, string OwnerWorkId,
    string RootBudgetId, ImmutableArray<string> AllowedKinds, ImmutableArray<string> GrantedCapabilities)
{
    public bool IsValid => EventEnvelope.Valid(PluginId) && Generation >= 1 && EventEnvelope.Valid(WorkspaceId)
        && EventEnvelope.Valid(OwnerWorkId) && EventEnvelope.Valid(RootBudgetId)
        && !AllowedKinds.IsDefault && !GrantedCapabilities.IsDefault;
}

/// <summary>
/// PA-07A host adapter: turns the plugin-facing event/job requests into the internal ports, binds ownership,
/// enforces generation fences and never lets a long job ride on one IPC request.
/// </summary>
public sealed class DeliveryCoordinator
{
    private readonly ISubscriptionStore _subscriptions;
    private readonly SubscriptionDispatcher _dispatcher;
    private readonly IEventDeliveryStore _deliveries;
    private readonly IPluginJobStore _jobs;
    private readonly TimeProvider _clock;

    public DeliveryCoordinator(ISubscriptionStore subscriptions, SubscriptionDispatcher dispatcher,
        IEventDeliveryStore deliveries, IPluginJobStore jobs, TimeProvider? clock = null)
    {
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PluginSubscriptionDescriptor> SubscribeAsync(HostPluginBinding binding,
        PluginSubscriptionRequest request, CancellationToken ct = default)
    {
        EnsureBinding(binding);
        if (request is null || request.Kinds.IsDefaultOrEmpty
            || !string.Equals(request.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal))
            throw Public(EventingErrorCodes.RequestInvalid, "A workspace-bound kind set is required.");
        if (request.Kinds.Distinct(StringComparer.Ordinal).Count() != request.Kinds.Length)
            throw Public(EventingErrorCodes.RequestInvalid, "Duplicate event kinds are not allowed.");
        foreach (var kind in request.Kinds)
        {
            if (!EventEnvelope.Valid(kind) || !binding.AllowedKinds.Contains(kind, StringComparer.Ordinal))
                throw Public(EventingErrorCodes.GrantDenied, "The plugin is not granted this event kind.");
        }
        var record = await _subscriptions.CreateAsync(new(binding.PluginId, binding.Generation, binding.WorkspaceId,
            request.Kinds), ct).ConfigureAwait(false);
        return new(record.SubscriptionId, record.Grant.Generation, record.CreatedAt);
    }

    public async Task<PluginEventPage> ReadAsync(HostPluginBinding binding, PluginEventReadRequest request,
        CancellationToken ct = default)
    {
        EnsureBinding(binding);
        if (request is null || request.MaxCount is < 1 or > SubscriptionDispatcher.MaxBatch)
            throw Public(EventingErrorCodes.RequestInvalid, "A bounded read is required.");
        var record = await LoadOwnedAsync(binding, request.SubscriptionId, ct).ConfigureAwait(false);
        EventDispatchResult result;
        try
        {
            result = await _dispatcher.DispatchAsync(record.SubscriptionId, OwnerOf(binding), request.MaxCount, ct: ct)
                .ConfigureAwait(false);
        }
        catch (EventingException ex) { throw Public(ex.Code, ex.Message); }
        return new(result.Deliveries.Select(ToPublic).ToImmutableArray(), result.SnapshotRequired, result.OldestAvailableSequence);
    }

    /// <summary>Acknowledgement is the only operation that advances the durable checkpoint.</summary>
    public async Task AckAsync(HostPluginBinding binding, PluginEventAck request, CancellationToken ct = default)
    {
        EnsureBinding(binding);
        if (request is null || request.EventIds.IsDefaultOrEmpty)
            throw Public(EventingErrorCodes.RequestInvalid, "At least one event id must be acknowledged.");
        var record = await LoadOwnedAsync(binding, request.SubscriptionId, ct).ConfigureAwait(false);
        if (record.Cancelled) throw Public(EventingErrorCodes.SubscriptionCancelled, "The subscription was cancelled.");
        var pending = await _deliveries.ListPendingAsync(record.SubscriptionId, ct).ConfigureAwait(false);
        // The plugin acknowledges logical events; the host maps them to leased delivery ids.
        var deliveryIds = ImmutableArray.CreateBuilder<string>();
        foreach (var eventId in request.EventIds)
        {
            var delivery = pending.FirstOrDefault(d => string.Equals(d.Event.EventId, eventId, StringComparison.Ordinal));
            if (delivery is null)
                throw Public(EventingErrorCodes.RequestInvalid, "The event is not currently leased to this subscription.");
            deliveryIds.Add(delivery.DeliveryId);
        }
        try
        {
            await _dispatcher.AckAsync(record.SubscriptionId, OwnerOf(binding), pending, deliveryIds.ToImmutable(), ct)
                .ConfigureAwait(false);
        }
        catch (EventingException ex) { throw Public(ex.Code, ex.Message); }
    }

    public async Task UnsubscribeAsync(HostPluginBinding binding, PluginSubscriptionRef request,
        CancellationToken ct = default)
    {
        EnsureBinding(binding);
        var record = await LoadOwnedAsync(binding, request?.SubscriptionId, ct).ConfigureAwait(false);
        await _subscriptions.CancelAsync(record.SubscriptionId, ct).ConfigureAwait(false);
    }

    /// <summary>Submit persists the job and returns immediately; the caller must release the worker slot while waiting.</summary>
    public async Task<PluginJobReceipt> SubmitJobAsync(HostPluginBinding binding, SubmitPluginJob request,
        CancellationToken ct = default)
    {
        EnsureJobBinding(binding);
        if (request is null || !EventEnvelope.Valid(request.Kind) || !EventEnvelope.Valid(request.InputRef)
            || !EventEnvelope.Valid(request.InputHash) || request.TimeoutSeconds is < 1 or > 3600
            || request.RequiredGrants.IsDefault)
            throw new PluginJobException(JobErrorCodes.RequestInvalid, "A bounded, input-referenced job is required.");
        foreach (var grant in request.RequiredGrants)
        {
            if (!EventEnvelope.Valid(grant) || !binding.GrantedCapabilities.Contains(grant, StringComparer.Ordinal))
                throw new PluginJobException(JobErrorCodes.GrantMissing, "The plugin lacks a required job grant.");
        }
        var now = _clock.GetUtcNow();
        var deadline = now.AddSeconds(request.TimeoutSeconds);
        if (deadline > now.AddDays(1)) throw new PluginJobException(JobErrorCodes.RequestInvalid, "The job deadline is out of bounds.");
        var internalRequest = new PluginJobRequest(request.Kind, StableOperationId(binding, request), request.InputRef,
            request.InputHash, request.TimeoutSeconds, request.RequiredGrants);
        var internalBinding = new PluginJobBinding(binding.PluginId, binding.Generation, binding.WorkspaceId,
            binding.OwnerWorkId, binding.RootBudgetId, deadline);
        try
        {
            var ticket = await _jobs.SubmitAsync(new(NewId(), internalRequest, internalBinding, now, 0), ct).ConfigureAwait(false);
            return new(ticket.JobId, ticket.AcceptedSequence);
        }
        catch (JobException ex) { throw new PluginJobException(ex.Code, ex.Message); }
    }

    public async Task<PluginJobView> GetJobAsync(HostPluginBinding binding, string jobId, CancellationToken ct = default)
    {
        EnsureJobBinding(binding);
        var snapshot = await LoadOwnedJobAsync(binding, jobId, ct).ConfigureAwait(false);
        return ToPublic(snapshot);
    }

    public async Task<PluginJobView> CancelJobAsync(HostPluginBinding binding, CancelPluginJob request,
        CancellationToken ct = default)
    {
        EnsureJobBinding(binding);
        var owned = await LoadOwnedJobAsync(binding, request?.JobId, ct).ConfigureAwait(false);
        try
        {
            var snapshot = await _jobs.CancelAsync(owned.JobId, binding.Generation, "plugin.cancelled", ct).ConfigureAwait(false);
            return ToPublic(snapshot);
        }
        catch (JobException ex) { throw new PluginJobException(ex.Code, ex.Message); }
    }

    private async ValueTask<SubscriptionRecord> LoadOwnedAsync(HostPluginBinding binding, string? subscriptionId,
        CancellationToken ct)
    {
        if (!EventEnvelope.Valid(subscriptionId))
            throw Public(EventingErrorCodes.RequestInvalid, "A subscription id is required.");
        var record = await _subscriptions.GetAsync(subscriptionId!, ct).ConfigureAwait(false);
        if (record is not { IsValid: true })
            throw Public(EventingErrorCodes.SubscriptionMissing, "The subscription is missing.");
        if (!string.Equals(record.Grant.PluginId, binding.PluginId, StringComparison.Ordinal)
            || !string.Equals(record.Grant.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || record.Grant.Generation != binding.Generation)
            throw Public(EventingErrorCodes.GenerationStale, "The subscription belongs to another plugin generation.");
        return record;
    }

    private async ValueTask<PluginJobSnapshot> LoadOwnedJobAsync(HostPluginBinding binding, string? jobId,
        CancellationToken ct)
    {
        if (!EventEnvelope.Valid(jobId)) throw new PluginJobException(JobErrorCodes.RequestInvalid, "A job id is required.");
        PluginJobSnapshot snapshot;
        try { snapshot = await _jobs.GetAsync(jobId!, ct).ConfigureAwait(false); }
        catch (JobException ex) { throw new PluginJobException(ex.Code, ex.Message); }
        if (!string.Equals(snapshot.PluginId, binding.PluginId, StringComparison.Ordinal)
            || !string.Equals(snapshot.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || snapshot.PluginGeneration != binding.Generation)
            throw new PluginJobException(JobErrorCodes.GenerationStale, "The job belongs to another plugin generation.");
        return snapshot;
    }

    private static void EnsureBinding(HostPluginBinding binding)
    {
        if (binding is null || !binding.IsValid)
            throw Public(EventingErrorCodes.RequestInvalid, "A valid host-bound plugin identity is required.");
    }

    private static void EnsureJobBinding(HostPluginBinding binding)
    {
        if (binding is null || !binding.IsValid)
            throw new PluginJobException(JobErrorCodes.BindingInvalid, "A valid host-bound plugin identity is required.");
    }

    private static PluginEventDelivery ToPublic(EventDelivery delivery) => new(delivery.Event.EventId,
        delivery.Event.Kind, delivery.Event.SchemaVersion, delivery.Event.OccurredAt, delivery.Event.WorkspaceId,
        delivery.Event.PayloadRef, delivery.Event.PayloadHash, delivery.Event.CausationId, delivery.Event.CorrelationId,
        delivery.Event.BusinessRef);

    private static PluginJobView ToPublic(PluginJobSnapshot snapshot) => new(snapshot.JobId,
        snapshot.State.ToString(), snapshot.PluginGeneration, snapshot.ReasonCode, snapshot.ResultRef,
        snapshot.ResultHash, snapshot.UpdatedAt, snapshot.Attempt);

    private static PluginEventsException Public(string code, string message) => new(code, message);

    /// <summary>A plugin generation is the lease owner: it cannot retry deliveries leased to another generation.</summary>
    private static string OwnerOf(HostPluginBinding binding)
        => "plugin-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(binding.PluginId + "|" + binding.Generation)))[..32];

    /// <summary>The plugin-facing request has no operation id; the host derives a stable one from the business key
    /// (owner work + kind + input ref) so a retried submit is idempotent while a changed payload still conflicts.</summary>
    private static string StableOperationId(HostPluginBinding binding, SubmitPluginJob request)
    {
        var raw = $"{binding.OwnerWorkId}|{request.Kind}|{request.InputRef}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        return "job-" + hash[..59];
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
