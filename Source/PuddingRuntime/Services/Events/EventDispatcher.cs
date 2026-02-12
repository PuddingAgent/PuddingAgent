using PuddingCode.Abstractions;
using PuddingCode.Events;
using PuddingCode.Models;
using PuddingCode.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 事件分发器 BackgroundService — 从优先级队列持续出队，按事件类型模式匹配
/// 路由到 IEventHandler 集合中的处理者。事件系统不感知处理者的具体实现。
/// 
/// 分发规则：
///   · 出队顺序：Urgent(10) → Important(5) → Normal(0)，同级 FIFO
///   · 类型匹配：IEventHandler.EventTypePattern 支持通配符，如 "cron.*" "agent.*"
///   · 多处理者：同一事件可被多个匹配的 handler 消费（fire-and-forget 并行）
///   · 无匹配：记警告日志，标记 completed（静默丢弃，不阻塞队列）
///   · 失败处理：handler 返回 false → 重试（指数退避，最多 3 次）→ 死信
/// </summary>
public class EventDispatcher : BackgroundService
{
    private readonly IPriorityEventQueue _queue;
    private readonly IEnumerable<IEventHandler> _handlers;
    private readonly ILogger<EventDispatcher> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;

    // 匹配缓存：eventType → handler 索引列表
    // 编译正则以加速高频匹配
    private readonly Dictionary<string, Regex> _patternRegexCache = new(StringComparer.OrdinalIgnoreCase);

    public EventDispatcher(
        IPriorityEventQueue queue,
        IEnumerable<IEventHandler> handlers,
        ILogger<EventDispatcher> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor)
    {
        _queue = queue;
        _handlers = handlers;
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"[Startup] EventDispatcher.ExecuteAsync started, {_handlers.Count()} handlers");
        _logger.LogInformation("[EventDispatcher] Started. Handlers registered: {Count}",
            _handlers.Count());

        foreach (var h in _handlers)
            _logger.LogInformation("[EventDispatcher]   Handler: pattern={Pattern} interrupts={Interrupts}",
                h.EventTypePattern, h.SupportsInterruption);

        // 诊断：每 30 秒输出队列统计
        _ = PeriodicStatsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var qe = await _queue.DequeueAsync(stoppingToken);

                if (qe is null)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "[EventDispatcher] Dequeued id={Id} type={Type} pri={Priority} retry={Retry}",
                    qe.Id, qe.EventType, qe.Priority, qe.RetryCount);

                var trace = ResolveTrace(qe);
                await RecordActivityAsync(trace, "dequeue", RuntimeActivityStatuses.Succeeded, qe, null, stoppingToken);

                var matchedHandlers = MatchHandlers(qe.EventType);

                if (matchedHandlers.Count == 0)
                {
                    var registeredPatterns = string.Join(", ", _handlers.Select(h => h.EventTypePattern));
                    _logger.LogWarning(
                        "[EventDispatcher] No handler for event id={Id} type={Type}. Registered patterns: [{Patterns}]. Silent drop.",
                        qe.Id, qe.EventType, registeredPatterns);
                    await _queue.UpdateStatusAsync(qe.Id, "completed", ct: stoppingToken);
                    await RecordActivityAsync(trace, "dispatch", RuntimeActivityStatuses.Deferred, qe, "No matching handler", stoppingToken);
                    continue;
                }

                // 反序列化负载为 InternalEvent
                var internalEvent = DeserializeEvent(qe);

                // Schema 版本兼容性检查（ARCH-EVENT-002）
                var currentVersion = EventSchemaRegistry.GetSchemaVersion(internalEvent.Type);
                var compatResult = EventSchemaRegistry.CheckCompatibility(
                    internalEvent.Type, internalEvent.SchemaVersion, currentVersion);

                if (!compatResult.IsCompatible)
                {
                    _logger.LogError(
                        "[EventDispatcher] Schema incompatible id={Id} type={Type} eventVersion={EventVer} registryVersion={RegVer} reason={Reason}",
                        qe.Id, qe.EventType, internalEvent.SchemaVersion, currentVersion, compatResult.BreakingChangeDescription);
                    await _queue.UpdateStatusAsync(qe.Id, "dead_letter",
                        $"Schema incompatible: {compatResult.BreakingChangeDescription}", ct: stoppingToken);
                    await RecordActivityAsync(trace, "dispatch", RuntimeActivityStatuses.Failed, qe,
                        $"Schema incompatible: {compatResult.BreakingChangeDescription}", stoppingToken);
                    continue;
                }

                if (internalEvent.SchemaVersion < currentVersion)
                {
                    _logger.LogWarning(
                        "[EventDispatcher] Schema version downgrade id={Id} type={Type} eventVersion={EventVer} registryVersion={RegVer}",
                        qe.Id, qe.EventType, internalEvent.SchemaVersion, currentVersion);
                }

                _traceAccessor.Current = internalEvent.Trace ?? trace;

                // 并行分发给所有匹配的 handler
                var results = await Task.WhenAll(
                    matchedHandlers.Select(h => SafeHandleAsync(h, internalEvent, stoppingToken)));

                var allSuccess = results.All(r => r);

                if (allSuccess)
                {
                    await _queue.UpdateStatusAsync(qe.Id, "completed", ct: stoppingToken);
                    _logger.LogInformation(
                        "[EventDispatcher] Completed id={Id} type={Type} handlers={Count}",
                        qe.Id, qe.EventType, matchedHandlers.Count);
                    await RecordActivityAsync(trace, "dispatch", RuntimeActivityStatuses.Succeeded, qe,
                        $"Handled by {matchedHandlers.Count} handler(s)", stoppingToken);
                }
                else
                {
                    // 队列层做最终裁决：retrying 可能在内部因 retry_count 超限转为 dead_letter
                    var finalStatus = await _queue.UpdateStatusAsync(qe.Id, "retrying",
                        $"Handler(s) failed for event type {qe.EventType}",
                        ct: stoppingToken);

                    if (finalStatus == "dead_letter")
                    {
                        _logger.LogError(
                            "[EventDispatcher] Dead letter id={Id} type={Type} retriesExhausted",
                            qe.Id, qe.EventType);
                        await RecordActivityAsync(trace, "dispatch", RuntimeActivityStatuses.Failed, qe,
                            "Max retries exhausted", stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[EventDispatcher] Retrying id={Id} type={Type} status={Status}",
                            qe.Id, qe.EventType, finalStatus);
                        await RecordActivityAsync(trace, "dispatch", RuntimeActivityStatuses.Retried, qe,
                            $"Retrying (queue status={finalStatus})", stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EventDispatcher] Dispatch loop error");
            }
        }

        _logger.LogInformation("[EventDispatcher] Stopped.");
    }

    /// <summary>诊断：每 30 秒输出队列水位统计</summary>
    private async Task PeriodicStatsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var stats = await _queue.GetStatsAsync(ct);
                _logger.LogInformation(
                    "[EventDispatcher:Stats] Queue U={Urgent} I={Important} N={Normal} Processing={Processing}",
                    stats.UrgentPending, stats.ImportantPending, stats.NormalPending, stats.Processing);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "[EventDispatcher:Stats] Stats collection failed"); }
        }
    }

    /// <summary>根据事件类型匹配所有 handler。</summary>
    private List<IEventHandler> MatchHandlers(string eventType)
    {
        var matched = new List<IEventHandler>();
        foreach (var handler in _handlers)
        {
            var regex = GetOrCompilePattern(handler.EventTypePattern);
            if (regex.IsMatch(eventType))
                matched.Add(handler);
        }
        return matched;
    }

    private Regex GetOrCompilePattern(string pattern)
    {
        if (!_patternRegexCache.TryGetValue(pattern, out var regex))
        {
            var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
            regex = new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _patternRegexCache[pattern] = regex;
        }
        return regex;
    }

    /// <summary>带异常保护地调用 handler。</summary>
    private async Task<bool> SafeHandleAsync(IEventHandler handler, InternalEvent evt, CancellationToken ct)
    {
        try
        {
            return await handler.HandleAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[EventDispatcher] Handler exception pattern={Pattern} eventType={Type} eventId={Id}",
                handler.EventTypePattern, evt.Type, evt.EventId);
            return false;
        }
    }

    private static InternalEvent DeserializeEvent(QueuedEvent qe)
    {
        object? payload = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(qe.Payload) && qe.Payload != "{}")
                payload = JsonSerializer.Deserialize<object>(qe.Payload);
        }
        catch { /* non-fatal */ }

        return new InternalEvent
        {
            EventId = qe.Id,
            Type = qe.EventType,
            SchemaVersion = qe.SchemaVersion > 0 ? qe.SchemaVersion : 1,
            CausationId = qe.CausationId,
            Priority = qe.Priority switch
            {
                >= 10 => EventPriorityLevel.Urgent,
                >= 5 => EventPriorityLevel.Important,
                _ => EventPriorityLevel.Normal,
            },
            Source = new EventSource
            {
                SourceType = qe.SourceType ?? "unknown",
                SourceId = qe.SourceId,
                ConnectorId = qe.ConnectorId,
            },
            SessionId = qe.SessionId,
            WorkspaceId = qe.WorkspaceId ?? "default",
            AgentId = qe.AgentId,
            Payload = payload,
            TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(qe.CreatedAt).UtcDateTime,
            TraceId = qe.Trace?.TraceId,
            CorrelationId = qe.Trace?.CorrelationId,
            Trace = qe.Trace,
        };
    }

    private RuntimeTraceContext ResolveTrace(QueuedEvent qe)
    {
        var trace = qe.Trace
            ?? _traceAccessor.Current
            ?? RuntimeTraceContext.CreateNew(
                sessionId: qe.SessionId,
                workspaceId: qe.WorkspaceId,
                eventId: qe.Id);

        trace = trace.WithEvent(qe.Id);
        _traceAccessor.Current = trace;
        return trace;
    }

    private Task RecordActivityAsync(
        RuntimeTraceContext trace,
        string operation,
        string status,
        QueuedEvent qe,
        string? summary,
        CancellationToken ct)
    {
        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.EventDispatcher,
            Operation = operation,
            Status = status,
            Summary = summary,
            Metadata = new Dictionary<string, string>
            {
                ["eventId"] = qe.Id,
                ["eventType"] = qe.EventType,
                ["priority"] = qe.Priority.ToString(),
                ["retryCount"] = qe.RetryCount.ToString(),
            },
        }, ct);
    }
}
