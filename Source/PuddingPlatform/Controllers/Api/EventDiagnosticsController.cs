using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Diagnostics;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 事件回放与诊断 API — 按 session/agent/trace 查询事件流，因果链回溯，事件统计。
/// ARCH-EVENT-004：提供最小的事件查询与回放能力。
/// </summary>
[Authorize]
[ApiController]
[Route("api/diagnostics/events")]
public sealed class EventDiagnosticsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<EventDiagnosticsController> _logger;

    public EventDiagnosticsController(
        PlatformDbContext db,
        ILogger<EventDiagnosticsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 综合事件查询：按 sessionId / traceId / agentId 过滤。
    /// GET /api/diagnostics/events?sessionId=xxx&amp;offset=0&amp;limit=100&amp;status=pending
    /// GET /api/diagnostics/events?traceId=xxx
    /// GET /api/diagnostics/events?agentId=xxx&amp;offset=0&amp;limit=100
    /// 支持分页（offset / limit）和状态过滤（status），按时间升序排列。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetEvents(
        [FromQuery] string? sessionId,
        [FromQuery] string? traceId,
        [FromQuery] string? agentId,
        [FromQuery] string? status,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new { message = "limit 必须在 1-500 之间" });

        if (offset < 0)
            return BadRequest(new { message = "offset 必须 >= 0" });

        var query = _db.EventQueue.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);

        if (!string.IsNullOrWhiteSpace(traceId))
            query = query.Where(e => e.TraceId == traceId);

        if (!string.IsNullOrWhiteSpace(agentId))
            query = query.Where(e => e.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);

        var totalCount = await query.CountAsync(ct);

        var events = await query
            .OrderBy(e => e.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(e => ToSummary(e))
            .ToListAsync(ct);

        _logger.LogDebug(
            "[EventDiagnostics] Query session={SessionId} trace={TraceId} agent={AgentId} status={Status} total={TotalCount} returned={Count}",
            sessionId, traceId, agentId, status, totalCount, events.Count);

        return Ok(new
        {
            totalCount,
            offset,
            limit,
            events,
        });
    }

    /// <summary>
    /// 事件统计：按 status / component（source_type）分组计数。
    /// GET /api/diagnostics/events/stats
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var byStatus = await _db.EventQueue.AsNoTracking()
            .GroupBy(e => e.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var byComponent = await _db.EventQueue.AsNoTracking()
            .GroupBy(e => e.SourceType ?? "unknown")
            .Select(g => new { component = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var totalCount = byStatus.Sum(s => s.count);

        _logger.LogDebug(
            "[EventDiagnostics] Stats total={TotalCount} statusGroups={StatusGroups} componentGroups={ComponentGroups}",
            totalCount, byStatus.Count, byComponent.Count);

        return Ok(new EventStatsDto
        {
            TotalCount = totalCount,
            ByStatus = byStatus.Select(s => new EventStatusCountDto { Status = s.status, Count = s.count }).ToList(),
            ByComponent = byComponent.Select(c => new EventComponentCountDto { Component = c.component, Count = c.count }).ToList(),
        });
    }

    /// <summary>
    /// 因果链回溯：通过 causationId 链从指定事件回溯到根事件。
    /// GET /api/diagnostics/events/{eventId}/trace
    /// 返回从根事件到目标事件的有序列表。
    /// </summary>
    [HttpGet("{eventId}/trace")]
    public async Task<IActionResult> GetCausalTrace(string eventId, CancellationToken ct)
    {
        var chain = new List<EventSummary>();
        var visited = new HashSet<string>();
        string? currentId = eventId;

        // 从目标事件向前回溯至根事件
        while (currentId != null)
        {
            if (!visited.Add(currentId))
            {
                _logger.LogWarning(
                    "[EventDiagnostics] Circular causation chain detected at event={EventId}", currentId);
                break;
            }

            var entity = await _db.EventQueue.AsNoTracking()
                .FirstOrDefaultAsync(e => e.EventId == currentId, ct);

            if (entity == null)
                break;

            chain.Add(ToSummary(entity));
            currentId = entity.CausationId;
        }

        // 反转使顺序为根→目标
        chain.Reverse();

        _logger.LogDebug(
            "[EventDiagnostics] Causal trace event={EventId} depth={Depth}",
            eventId, chain.Count);

        return Ok(new
        {
            targetEventId = eventId,
            depth = chain.Count,
            chain,
        });
    }

    /// <summary>
    /// 将 EventQueueEntity 转为脱敏摘要：不含完整 payload，返回 payloadSize / payloadPreview。
    /// error_message 完整返回（诊断需要）。
    /// </summary>
    private static EventSummary ToSummary(EventQueueEntity e)
    {
        var payloadSize = Encoding.UTF8.GetByteCount(e.Payload);
        var payloadPreview = e.Payload.Length > 200
            ? e.Payload[..200] + "..."
            : e.Payload;

        return new EventSummary
        {
            Id = e.Id,
            EventId = e.EventId,
            Priority = e.Priority,
            EventType = e.EventType,
            SourceType = e.SourceType,
            SourceId = e.SourceId,
            ConnectorId = e.ConnectorId,
            SessionId = e.SessionId,
            WorkspaceId = e.WorkspaceId,
            AgentId = e.AgentId,
            Status = e.Status,
            RetryCount = e.RetryCount,
            AvailableAt = e.AvailableAt,
            LeaseUntil = e.LeaseUntil,
            StartedAt = e.StartedAt,
            CompletedAt = e.CompletedAt,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
            ErrorMessage = e.ErrorMessage,
            SchemaVersion = e.SchemaVersion,
            CausationId = e.CausationId,
            TraceId = e.TraceId,
            CorrelationId = e.CorrelationId,
            ExecutionId = e.ExecutionId,
            ParentExecutionId = e.ParentExecutionId,
            SubAgentId = e.SubAgentId,
            UserId = e.UserId,
            PayloadSize = payloadSize,
            PayloadPreview = payloadPreview,
        };
    }

    /// <summary>
    /// 脱敏后的事件摘要 DTO，用 payloadSize + payloadPreview 替代完整 payload。
    /// </summary>
    public sealed class EventSummary
    {
        public long Id { get; init; }
        public string EventId { get; init; } = string.Empty;
        public int Priority { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string? SourceType { get; init; }
        public string? SourceId { get; init; }
        public string? ConnectorId { get; init; }
        public string? SessionId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? AgentId { get; init; }
        public string Status { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public long AvailableAt { get; init; }
        public long? LeaseUntil { get; init; }
        public long? StartedAt { get; init; }
        public long? CompletedAt { get; init; }
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; init; }
        public string? ErrorMessage { get; init; }
        public int SchemaVersion { get; init; }
        public string? CausationId { get; init; }
        public string? TraceId { get; init; }
        public string? CorrelationId { get; init; }
        public string? ExecutionId { get; init; }
        public string? ParentExecutionId { get; init; }
        public string? SubAgentId { get; init; }
        public string? UserId { get; init; }
        public int PayloadSize { get; init; }
        public string PayloadPreview { get; init; } = string.Empty;
    }
}
