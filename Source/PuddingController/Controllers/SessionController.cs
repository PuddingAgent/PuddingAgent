using Microsoft.AspNetCore.Mvc;
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
}
