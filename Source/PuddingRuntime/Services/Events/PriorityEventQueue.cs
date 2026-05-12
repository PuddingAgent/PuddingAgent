using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 优先级事件队列 — SQLite 持久化实现。
/// 三级优先级：Urgent(10) > Important(5) > Normal(0)。
/// 出队时严格按优先级降序 + 同优先级按 CreatedAt 升序。
/// 
/// Phase 2 骨架：接口定义 + 内存回退。Phase 3 接入 SQLite。
/// </summary>
public class PriorityEventQueue : IPriorityEventQueue
{
    private readonly ILogger<PriorityEventQueue> _logger;

    // Phase 2: 内存队列占位（后续替换为 SQLite）
    private readonly Queue<QueuedEvent> _urgentQueue = new();
    private readonly Queue<QueuedEvent> _importantQueue = new();
    private readonly Queue<QueuedEvent> _normalQueue = new();
    private readonly object _lock = new();

    public PriorityEventQueue(ILogger<PriorityEventQueue> logger)
    {
        _logger = logger;
        _logger.LogInformation("[PriorityEventQueue] Initialized: Phase 2 in-memory mode");
    }

    public Task<string> EnqueueAsync(ProcessedEvent evt, EventPriorityLevel priority, CancellationToken ct = default)
    {
        var qe = new QueuedEvent
        {
            Id = evt.EventId,
            Priority = (int)priority,
            EventType = evt.Type,
            SourceType = evt.Source.SourceType,
            SourceId = evt.Source.SourceId,
            Payload = System.Text.Json.JsonSerializer.Serialize(evt.Payload ?? new { }),
            Status = "pending",
            CreatedAt = evt.Timestamp,
        };

        lock (_lock)
        {
            switch (priority)
            {
                case EventPriorityLevel.Urgent:
                    _urgentQueue.Enqueue(qe);
                    break;
                case EventPriorityLevel.Important:
                    _importantQueue.Enqueue(qe);
                    break;
                default:
                    _normalQueue.Enqueue(qe);
                    break;
            }
        }

        _logger.LogDebug("[PriorityEventQueue] Enqueued {Id} type={Type} priority={Priority}",
            qe.Id, qe.EventType, qe.Priority);

        return Task.FromResult(qe.Id);
    }

    public Task<QueuedEvent?> DequeueAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_urgentQueue.Count > 0)
                return Task.FromResult<QueuedEvent?>(_urgentQueue.Dequeue());
            if (_importantQueue.Count > 0)
                return Task.FromResult<QueuedEvent?>(_importantQueue.Dequeue());
            if (_normalQueue.Count > 0)
                return Task.FromResult<QueuedEvent?>(_normalQueue.Dequeue());
        }
        return Task.FromResult<QueuedEvent?>(null);
    }

    public Task<QueuedEvent?> PeekAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_urgentQueue.Count > 0) return Task.FromResult<QueuedEvent?>(_urgentQueue.Peek());
            if (_importantQueue.Count > 0) return Task.FromResult<QueuedEvent?>(_importantQueue.Peek());
            if (_normalQueue.Count > 0) return Task.FromResult<QueuedEvent?>(_normalQueue.Peek());
        }
        return Task.FromResult<QueuedEvent?>(null);
    }

    public Task<QueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(new QueueStats
            {
                NormalPending = _normalQueue.Count,
                ImportantPending = _importantQueue.Count,
                UrgentPending = _urgentQueue.Count,
                Processing = 0,
            });
        }
    }

    public Task UpdateStatusAsync(string eventId, string status, string? errorMessage = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[PriorityEventQueue] Status updated: {Id} → {Status} err={Error}",
            eventId, status, errorMessage ?? "none");
        return Task.CompletedTask;
    }
}
