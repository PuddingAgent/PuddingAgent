using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Hooks;

/// <summary>
/// Thin adapter from Hook v2 lifecycle events to the existing internal event bus.
/// </summary>
public sealed class HookPublisher : IHookPublisher
{
    private readonly IInternalEventBus _eventBus;
    private readonly IRuntimeActivitySink _activitySink;

    public HookPublisher(
        IInternalEventBus eventBus,
        IRuntimeActivitySink activitySink)
    {
        _eventBus = eventBus;
        _activitySink = activitySink;
    }

    public async Task<string> PublishAsync<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name.Value))
            throw new ArgumentException("Hook event name cannot be empty.", nameof(name));

        options ??= new HookPublishOptions();
        var evt = BuildEvent(name, payload, options);
        var trace = evt.Trace ?? RuntimeTraceContext.CreateNew(
            sessionId: evt.SessionId,
            workspaceId: evt.WorkspaceId,
            eventId: evt.EventId,
            correlationId: evt.CorrelationId);

        try
        {
            await _eventBus.PublishAsync(evt with { Trace = trace, TraceId = trace.TraceId, CorrelationId = trace.CorrelationId }, ct);
            await RecordActivityAsync(trace, evt, RuntimeActivityStatuses.Succeeded, null, ct);
            return evt.EventId;
        }
        catch (Exception ex)
        {
            await RecordActivityAsync(trace, evt, RuntimeActivityStatuses.Failed, ex, CancellationToken.None);
            throw;
        }
    }

    private static InternalEvent BuildEvent<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions options)
    {
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: options.SessionId,
            workspaceId: options.WorkspaceId);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hook_event"] = name.Value,
        };

        if (!string.IsNullOrWhiteSpace(options.IdempotencyKey))
            metadata["idempotency_key"] = options.IdempotencyKey;

        return new InternalEvent
        {
            Type = name.Value,
            SchemaVersion = 1,
            CausationId = options.CausationId,
            Priority = options.Priority,
            Isolation = EventIsolationMode.Isolated,
            Source = new EventSource
            {
                SourceType = string.IsNullOrWhiteSpace(options.SourceType) ? "framework" : options.SourceType,
                SourceId = options.SourceId,
            },
            SessionId = options.SessionId,
            WorkspaceId = string.IsNullOrWhiteSpace(options.WorkspaceId) ? "default" : options.WorkspaceId,
            AgentId = options.AgentId,
            Payload = payload,
            Metadata = metadata,
            Trace = trace,
            TraceId = trace.TraceId,
            CorrelationId = trace.CorrelationId,
        };
    }

    private Task RecordActivityAsync(
        RuntimeTraceContext trace,
        InternalEvent evt,
        string status,
        Exception? error,
        CancellationToken ct)
    {
        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace.WithEvent(evt.EventId),
            Component = RuntimeActivityComponents.HookSystem,
            Operation = "hook.publish",
            Status = status,
            Severity = error is null ? "info" : "error",
            Summary = error is null
                ? $"Published hook event {evt.Type}"
                : $"Failed to publish hook event {evt.Type}",
            ErrorCode = error?.GetType().Name,
            ErrorMessage = error?.Message,
            Metadata = new Dictionary<string, string>
            {
                ["hook_event"] = evt.Type,
                ["event_id"] = evt.EventId,
                ["workspace_id"] = evt.WorkspaceId,
                ["source_type"] = evt.Source.SourceType,
            },
        }, ct);
    }
}
