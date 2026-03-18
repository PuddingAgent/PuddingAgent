using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Session 历史查询 JSON API（供 Admin SPA 调用）。</summary>
[ApiController]
[Route("api/sessions")]
public class SessionApiController : ControllerBase
{
    private readonly PlatformApiClient _api;

    public SessionApiController(PlatformApiClient api) => _api = api;

    /// <summary>GET /api/sessions?workspaceId=xxx</summary>
    [HttpGet]
    public async Task<ActionResult<List<SessionRecord>>> List(
        [FromQuery] string? workspaceId, CancellationToken ct)
    {
        var list = await _api.GetSessionsAsync(workspaceId, ct);
        return Ok(list);
    }

    /// <summary>GET /api/sessions/{sessionId}</summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionRecord>> Get(string sessionId, CancellationToken ct)
    {
        var session = await _api.GetSessionAsync(sessionId, ct);
        return session is null ? NotFound() : Ok(session);
    }
}
