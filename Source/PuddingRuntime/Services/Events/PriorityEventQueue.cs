using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using System.Text.Json;

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
    private const int MaxRetries = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly ILogger<PriorityEventQueue> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();

    public PriorityEventQueue(
        ILogger<PriorityEventQueue> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
        _scopeFactory = scopeFactory;
        _logger.LogInformation("[PriorityEventQueue] Initialized: SQLite-backed mode");
    }

    public async Task<string> EnqueueAsync(ProcessedEvent evt, EventPriorityLevel priority, CancellationToken ct = default)
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

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new EventQueueEntity
        {
            EventId = evt.EventId,
            Priority = (int)priority,
            EventType = evt.Type,
            SourceType = evt.Source.SourceType,
            SourceId = evt.Source.SourceId,
            ConnectorId = evt.Source.ConnectorId,
            SessionId = evt.SessionId,
            WorkspaceId = evt.WorkspaceId,
            AgentId = evt.AgentId,
            Payload = JsonSerializer.Serialize(evt.Payload ?? new { }),
            Status = "pending",
            AvailableAt = now,
            CreatedAt = evt.Timestamp > 0 ? evt.Timestamp : now,
            UpdatedAt = now,
            TraceId = trace.TraceId,
            CorrelationId = trace.CorrelationId,
            ExecutionId = trace.ExecutionId,
            ParentExecutionId = trace.ParentExecutionId,
            SubAgentId = trace.SubAgentId,
            UserId = trace.UserId,
            // SchemaVersion / CausationId 从 ProcessedEvent 暂缺，待 ProcessedEvent 标准化后填充
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var existing = await db.EventQueue.FirstOrDefaultAsync(q => q.EventId == evt.EventId, ct);
        if (existing is null)
        {
            db.EventQueue.Add(entity);
        }
        else if (existing.Status is "completed" or "dead_letter")
        {
            existing.Priority = entity.Priority;
            existing.EventType = entity.EventType;
            existing.SourceType = entity.SourceType;
            existing.SourceId = entity.SourceId;
            existing.ConnectorId = entity.ConnectorId;
            existing.SessionId = entity.SessionId;
            existing.WorkspaceId = entity.WorkspaceId;
            existing.AgentId = entity.AgentId;
            existing.Payload = entity.Payload;
            existing.Status = "pending";
            existing.RetryCount = 0;
            existing.AvailableAt = now;
            existing.LeaseUntil = null;
            existing.StartedAt = null;
            existing.CompletedAt = null;
            existing.ErrorMessage = null;
            existing.UpdatedAt = now;
            existing.TraceId = entity.TraceId;
            existing.CorrelationId = entity.CorrelationId;
            existing.ExecutionId = entity.ExecutionId;
            existing.ParentExecutionId = entity.ParentExecutionId;
            existing.SubAgentId = entity.SubAgentId;
            existing.UserId = entity.UserId;
            existing.SchemaVersion = entity.SchemaVersion;
            existing.CausationId = entity.CausationId;
        }
        else
        {
            _logger.LogInformation(
                "[PriorityEventQueue] Duplicate active event ignored id={Id} status={Status}",
                evt.EventId,
                existing.Status);
            return existing.EventId;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogDebug("[PriorityEventQueue] Enqueued {Id} type={Type} priority={Priority}",
            entity.EventId, entity.EventType, entity.Priority);

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.EventQueue,
            Operation = "enqueue",
            Status = RuntimeActivityStatuses.Succeeded,
            Summary = $"Enqueued event {entity.EventType}",
            Metadata = new Dictionary<string, string>
            {
                ["eventId"] = entity.EventId,
                ["eventType"] = entity.EventType,
                ["priority"] = entity.Priority.ToString(),
            },
        }, ct);

        return entity.EventId;
    }

    public async Task<QueuedEvent?> DequeueAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leaseUntil = DateTimeOffset.UtcNow.Add(LeaseDuration).ToUnixTimeMilliseconds();

        lock (_lock)
        {
            var entity = db.EventQueue
                .Where(q => (q.Status == "pending" || q.Status == "retrying" || q.Status == "processing")
                    && q.AvailableAt <= now
                    && (q.LeaseUntil == null || q.LeaseUntil <= now))
                .OrderByDescending(q => q.Priority)
                .ThenBy(q => q.CreatedAt)
                .FirstOrDefault();

            if (entity is null)
                return null;

            if (entity.Status == "processing")
            {
                // 回收僵尸事件：消费者崩溃导致 processing 但 lease 已过期
                entity.Status = "processing";  // 保持 processing 状态
                entity.LeaseUntil = leaseUntil; // 立即赋予新 lease
                // RetryCount 不递增（这不是重试，是恢复）
                _logger.LogWarning("[PriorityEventQueue] Reclaimed zombie event id={Id} type={Type}",
                    entity.EventId, entity.EventType);
            }
            else
            {
                entity.Status = "processing";
                entity.StartedAt ??= now;
                entity.LeaseUntil = leaseUntil;
                // 如果原状态是 retrying，RetryCount 已在上次失败时递增，此处不重复递增
            }
            entity.UpdatedAt = now;
            db.SaveChanges();

            return ToQueuedEvent(entity);
        }
    }

    public async Task<QueuedEvent?> PeekAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var entity = await db.EventQueue
            .AsNoTracking()
            .Where(q => (q.Status == "pending" || q.Status == "retrying")
                && q.AvailableAt <= now
                && (q.LeaseUntil == null || q.LeaseUntil <= now))
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToQueuedEvent(entity);
    }

    public async Task<QueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var active = await db.EventQueue
            .AsNoTracking()
            .Where(q => q.Status == "pending" || q.Status == "retrying" || q.Status == "processing")
            .GroupBy(q => new { q.Status, q.Priority })
            .Select(g => new { g.Key.Status, g.Key.Priority, Count = g.Count() })
            .ToListAsync(ct);

        return new QueueStats
        {
            NormalPending = active.Where(x => (x.Status is "pending" or "retrying") && x.Priority < 5).Sum(x => x.Count),
            ImportantPending = active.Where(x => (x.Status is "pending" or "retrying") && x.Priority >= 5 && x.Priority < 10).Sum(x => x.Count),
            UrgentPending = active.Where(x => (x.Status is "pending" or "retrying") && x.Priority >= 10).Sum(x => x.Count),
            Processing = active.Where(x => x.Status == "processing").Sum(x => x.Count),
        };
    }

    /// <summary>诊断用：返回队列总数</summary>
    public int CountTotal()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return db.EventQueue.Count(q => q.Status == "pending" || q.Status == "retrying");
    }

    /// <summary>更新事件状态，返回最终生效的状态字符串（retrying 可能转为 dead_letter）。</summary>
    public async Task<string> UpdateStatusAsync(string eventId, string status, string? errorMessage = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entity = await db.EventQueue.FirstOrDefaultAsync(q => q.EventId == eventId, ct);
        if (entity is null)
        {
            _logger.LogWarning("[PriorityEventQueue] Status update ignored, event not found: {Id}", eventId);
            return "not_found";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.UpdatedAt = now;
        entity.ErrorMessage = errorMessage;

        switch (status)
        {
            case "completed":
                entity.Status = "completed";
                entity.CompletedAt = now;
                entity.LeaseUntil = null;
                break;
            case "retrying":
                entity.RetryCount++;
                if (entity.RetryCount >= MaxRetries)
                {
                    entity.Status = "dead_letter";
                    entity.CompletedAt = now;
                    entity.LeaseUntil = null;
                    entity.ErrorMessage = errorMessage ?? "Max retries exhausted";
                }
                else
                {
                    entity.Status = "retrying";
                    // 指数退避：min(2^retryCount * 10, 300) 秒
                    var backoffSeconds = Math.Min(Math.Pow(2, entity.RetryCount) * 10, 300);
                    entity.AvailableAt = DateTimeOffset.UtcNow
                        .AddSeconds(backoffSeconds)
                        .ToUnixTimeMilliseconds();
                    entity.LeaseUntil = null;
                }
                break;
            case "dead_letter":
                entity.Status = "dead_letter";
                entity.CompletedAt = now;
                entity.LeaseUntil = null;
                break;
            default:
                entity.Status = status;
                break;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("[PriorityEventQueue] Status updated: {Id} → {Status} err={Error}",
            eventId, entity.Status, errorMessage ?? "none");

        return entity.Status;
    }

    private static QueuedEvent ToQueuedEvent(EventQueueEntity entity)
    {
        RuntimeTraceContext? trace = null;
        if (!string.IsNullOrWhiteSpace(entity.TraceId) && !string.IsNullOrWhiteSpace(entity.CorrelationId))
        {
            trace = new RuntimeTraceContext
            {
                TraceId = entity.TraceId,
                CorrelationId = entity.CorrelationId,
                SessionId = entity.SessionId,
                WorkspaceId = entity.WorkspaceId,
                ExecutionId = entity.ExecutionId,
                ParentExecutionId = entity.ParentExecutionId,
                SubAgentId = entity.SubAgentId,
                EventId = entity.EventId,
                ConnectorId = entity.ConnectorId,
                UserId = entity.UserId,
            };
        }

        return new QueuedEvent
        {
            Id = entity.EventId,
            Priority = entity.Priority,
            SchemaVersion = entity.SchemaVersion,
            CausationId = entity.CausationId,
            EventType = entity.EventType,
            SourceType = entity.SourceType,
            SourceId = entity.SourceId,
            ConnectorId = entity.ConnectorId,
            SessionId = entity.SessionId,
            WorkspaceId = entity.WorkspaceId,
            AgentId = entity.AgentId,
            Payload = entity.Payload,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            RetryCount = entity.RetryCount,
            ErrorMessage = entity.ErrorMessage,
            Trace = trace,
        };
    }
}
