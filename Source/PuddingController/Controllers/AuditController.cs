using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>审计事件查询 API。</summary>
[ApiController]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    private readonly InMemoryAuditEventStore _auditStore;

    public AuditController(InMemoryAuditEventStore auditStore)
    {
        _auditStore = auditStore;
    }

    /// <summary>按会话/工作空间查询审计事件。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditEventRecord>>> Query(
        [FromQuery] string? sessionId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? messageId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var list = await _auditStore.QueryAsync(sessionId, messageId, workspaceId, limit: limit, ct: ct);
        return Ok(list);
    }

    /// <summary>按事件 ID 获取单条审计记录。</summary>
    [HttpGet("{eventId}")]
    public async Task<ActionResult<AuditEventRecord>> Get(string eventId, CancellationToken ct)
    {
        var record = await _auditStore.GetAsync(eventId, ct);
        return record is null ? NotFound() : Ok(record);
    }
}
