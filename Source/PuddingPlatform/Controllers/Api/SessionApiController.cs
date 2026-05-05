using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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
        return Ok(list.Where(x => x.Status != SessionStatus.Frozen).ToList());
    }

    /// <summary>GET /api/sessions/{sessionId}</summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionRecord>> Get(string sessionId, CancellationToken ct)
    {
        var session = await _api.GetSessionAsync(sessionId, ct);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>DELETE /api/sessions/{sessionId} — 删除会话</summary>
    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken ct)
    {
        await _api.DeleteSessionAsync(sessionId, ct);
        return NoContent();
    }

    /// <summary>PUT /api/sessions/{sessionId}/title — 重命名会话</summary>
    [HttpPut("{sessionId}/title")]
    public async Task<ActionResult<SessionRecord>> Rename(
        string sessionId, [FromBody] RenameSessionRequest req, CancellationToken ct)
    {
        var updateReq = new Services.UpdateSessionRequest { Title = req.Title };
        var session = await _api.UpdateSessionAsync(sessionId, updateReq, ct);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>
    /// POST /api/sessions/{sessionId}/archive — 归档会话。
    /// 将 Status 设为 Frozen（Frozen 在会话语境中即"已归档"，隐藏于列表但保留数据）。
    /// </summary>
    [HttpPost("{sessionId}/archive")]
    public async Task<ActionResult<SessionRecord>> Archive(string sessionId, CancellationToken ct)
    {
        var updateReq = new Services.UpdateSessionRequest { Status = SessionStatus.Frozen };
        var session = await _api.UpdateSessionAsync(sessionId, updateReq, ct);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>POST /api/sessions — 显式创建新会话</summary>
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<SessionRecord>> Create(
        [FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var session = await _api.CreateSessionAsync(req.WorkspaceId, req.AgentTemplateId, req.Title, ct);
        return session is null
            ? BadRequest()
            : CreatedAtAction(nameof(Get), new { sessionId = session.SessionId }, session);
    }
}

public sealed record RenameSessionRequest
{
    public required string Title { get; init; }
}

public sealed record CreateSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? Title { get; init; }
}
