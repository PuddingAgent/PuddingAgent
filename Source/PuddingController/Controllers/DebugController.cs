using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>调试诊断 API——查询路由决策、适配器状态等。</summary>
[ApiController]
[Route("api/[controller]")]
public class DebugController : ControllerBase
{
    private readonly InMemoryRouteDecisionStore _routeStore;
    private readonly InMemoryAuditEventStore _auditStore;

    public DebugController(
        InMemoryRouteDecisionStore routeStore,
        InMemoryAuditEventStore auditStore)
    {
        _routeStore = routeStore;
        _auditStore = auditStore;
    }

    /// <summary>按消息 ID 查询路由决策记录。</summary>
    [HttpGet("route/{messageId}")]
    public async Task<ActionResult<RouteDecisionRecord>> GetRouteDecision(string messageId, CancellationToken ct)
    {
        var record = await _routeStore.GetByMessageAsync(messageId, ct);
        return record is null ? NotFound() : Ok(record);
    }

    /// <summary>获取 Controller 运行概况。</summary>
    [HttpGet("summary")]
    public async Task<ActionResult> Summary(CancellationToken ct)
    {
        var recentAudit = await _auditStore.QueryAsync(limit: 10, ct: ct);
        return Ok(new
        {
            utcNow = DateTimeOffset.UtcNow,
            recentAuditCount = recentAudit.Count,
            recentAuditEvents = recentAudit.Select(e => new
            {
                e.EventId,
                e.EventType,
                e.MessageId,
                e.SessionId,
                e.Timestamp
            })
        });
    }
}
