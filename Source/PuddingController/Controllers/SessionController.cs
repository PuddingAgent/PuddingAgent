using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>Session 查询 API。</summary>
[ApiController]
[Route("api/[controller]")]
public class SessionController : ControllerBase
{
    private readonly InMemorySessionRepository _sessions;

    public SessionController(InMemorySessionRepository sessions)
    {
        _sessions = sessions;
    }

    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionRecord>> Get(string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpGet("workspace/{workspaceId}")]
    public async Task<ActionResult<IReadOnlyList<SessionRecord>>> ListByWorkspace(string workspaceId, CancellationToken ct)
    {
        var list = await _sessions.QueryAsync(workspaceId: workspaceId, ct: ct);
        return Ok(list);
    }

    /// <summary>DELETE /api/session/{sessionId} — 删除会话</summary>
    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken ct)
    {
        await _sessions.DeleteAsync(sessionId, ct);
        return NoContent();
    }

    /// <summary>PUT /api/session/{sessionId} — 更新会话（标题/状态等）</summary>
    [HttpPut("{sessionId}")]
    public async Task<ActionResult<SessionRecord>> Update(
        string sessionId, [FromBody] UpdateSessionRequest req, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null) return NotFound();

        var updated = session;
        if (req.Title is not null)
            updated = updated with { Title = req.Title };
        if (req.Status is not null)
            updated = updated with { Status = req.Status.Value };

        await _sessions.UpdateAsync(updated, ct);
        return Ok(updated);
    }

    /// <summary>POST /api/session — 显式创建新会话</summary>
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<SessionRecord>> Create(
        [FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "admin";
        var session = new SessionRecord
        {
            SessionId = Guid.NewGuid().ToString("N"),
            WorkspaceId = req.WorkspaceId,
            AgentTemplateId = req.AgentTemplateId,
            ChannelId = "admin",
            OwnerUserId = userId,
            SessionType = SessionType.ServiceSession,
            Status = SessionStatus.Active,
            Title = req.Title,
        };
        await _sessions.CreateAsync(session, ct);
        return CreatedAtAction(nameof(Get), new { sessionId = session.SessionId }, session);
    }
}

/// <summary>更新会话请求</summary>
public sealed record UpdateSessionRequest
{
    public string? Title { get; init; }
    public SessionStatus? Status { get; init; }
}

public sealed record CreateSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? Title { get; init; }
}
