using System.Collections.Concurrent;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 内存事件总线实现 — 基于 ConcurrentDictionary + 通配符匹配的进程内 pub/sub。
/// Phase 3: 当前实现为基础骨架，后续增加异步处理、背压、死信等。
/// </summary>
public class InternalEventBus : IInternalEventBus
{
    private readonly ILogger<InternalEventBus> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;
    private readonly ConcurrentDictionary<string, List<SubscriptionEntry>> _subscriptions = new();
    private readonly object _lock = new();

    public InternalEventBus(
        ILogger<InternalEventBus> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor)
    {
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
    }

    public async Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
    {
        var trace = ResolveTrace(evt);
        var matchedHandlers = new List<Func<InternalEvent, Task>>();

        lock (_lock)
        {
            foreach (var (pattern, entries) in _subscriptions)
            {
                if (MatchPattern(pattern, evt.Type))
                {
                    foreach (var entry in entries.Where(e => e.IsActive))
                    {
                        matchedHandlers.Add(entry.Handler);
                    }
                }
            }
        }

        if (matchedHandlers.Count == 0)
        {
            _logger.LogDebug("[InternalEventBus] No subscribers for event type={Type}", evt.Type);
            await RecordActivityAsync(trace, "publish", RuntimeActivityStatuses.Deferred, evt, "No subscribers", ct);
            return;
        }

        _logger.LogDebug("[InternalEventBus] Publishing {Type} to {Count} subscriber(s)",
            evt.Type, matchedHandlers.Count);

        await RecordActivityAsync(
            trace,
            "publish",
            RuntimeActivityStatuses.Succeeded,
            evt,
            $"Published to {matchedHandlers.Count} subscriber(s)",
            ct);

        // Fire and forget handlers (Phase 3: 后续考虑有序/串行执行)
        foreach (var handler in matchedHandlers)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(evt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[InternalEventBus] Handler error for event type={Type}", evt.Type);
                    await RecordActivityAsync(
                        trace,
                        "handler",
                        RuntimeActivityStatuses.Failed,
                        evt,
                        ex.Message,
                        CancellationToken.None);
                }
            }, ct);
        }
    }

    public Task<IEventSubscriptionHandle> SubscribeAsync(
        string eventTypePattern,
        Func<InternalEvent, Task> handler,
        CancellationToken ct = default)
    {
        var handle = new SubscriptionHandle(eventTypePattern, this);

        lock (_lock)
        {
            if (!_subscriptions.ContainsKey(eventTypePattern))
                _subscriptions[eventTypePattern] = [];

            _subscriptions[eventTypePattern].Add(new SubscriptionEntry(handler, handle));
        }

        _logger.LogInformation("[InternalEventBus] Subscribed: pattern={Pattern} id={Id}",
            eventTypePattern, handle.SubscriptionId);

        return Task.FromResult<IEventSubscriptionHandle>(handle);
    }

    public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
    {
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(handle.EventTypePattern, out var entries))
            {
                entries.RemoveAll(e => e.Handle.SubscriptionId == handle.SubscriptionId);
                if (entries.Count == 0)
                    _subscriptions.TryRemove(handle.EventTypePattern, out _);
            }
        }

        _logger.LogInformation("[InternalEventBus] Unsubscribed: id={Id}", handle.SubscriptionId);
        return Task.CompletedTask;
    }

    /// <summary>内部取消订阅（由 SubscriptionHandle.Dispose 调用）。</summary>
    internal void RemoveSubscription(SubscriptionHandle handle)
    {
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(handle.EventTypePattern, out var entries))
            {
                entries.RemoveAll(e => e.Handle.SubscriptionId == handle.SubscriptionId);
            }
        }
    }

    private static bool MatchPattern(string pattern, string eventType)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(eventType, regexPattern);
    }

    private RuntimeTraceContext ResolveTrace(InternalEvent evt)
    {
        var trace = evt.Trace
            ?? _traceAccessor.Current
            ?? RuntimeTraceContext.CreateNew(
                sessionId: evt.SessionId,
                workspaceId: evt.WorkspaceId,
                eventId: evt.EventId,
                connectorId: evt.Source.ConnectorId);

        trace = trace.WithEvent(evt.EventId);
        _traceAccessor.Current = trace;
        return trace;
    }

    private Task RecordActivityAsync(
        RuntimeTraceContext trace,
        string operation,
        string status,
        InternalEvent evt,
        string? summary,
        CancellationToken ct)
    {
        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.EventDispatcher,
            Operation = $"event_bus.{operation}",
            Status = status,
            Summary = summary,
            Metadata = new Dictionary<string, string>
            {
                ["eventType"] = evt.Type,
                ["eventId"] = evt.EventId,
                ["sourceType"] = evt.Source.SourceType,
            },
        }, ct);
    }

    private sealed record SubscriptionEntry(Func<InternalEvent, Task> Handler, SubscriptionHandle Handle)
    {
        public bool IsActive => Handle.IsActive;
    }
}

/// <summary>
/// 事件订阅句柄实现。
/// </summary>
internal sealed class SubscriptionHandle : IEventSubscriptionHandle
{
    private readonly InternalEventBus _bus;
    private bool _isActive = true;

    public SubscriptionHandle(string eventTypePattern, InternalEventBus bus)
    {
        SubscriptionId = Guid.NewGuid().ToString("N");
        EventTypePattern = eventTypePattern;
        _bus = bus;
    }

    public string SubscriptionId { get; }
    public string EventTypePattern { get; }
    public bool IsActive => _isActive;

    public void Dispose()
    {
        if (_isActive)
        {
            _isActive = false;
            _bus.RemoveSubscription(this);
        }
    }
}
