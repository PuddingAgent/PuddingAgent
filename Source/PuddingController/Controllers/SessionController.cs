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

    /// <summary>POST /api/session — 显式创建新会话（内部 API，由 PlatformApiClient 调用）</summary>
    [HttpPost]
    public async Task<ActionResult<SessionRecord>> Create(
        [FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "admin";
        var title = string.IsNullOrWhiteSpace(req.Title)
            ? await BuildDefaultTitleAsync(req.WorkspaceId, req.AgentTemplateId, req.DefaultTitleBase, ct)
            : req.Title.Trim();
        var session = new SessionRecord
        {
            SessionId = Guid.NewGuid().ToString("N"),
            WorkspaceId = req.WorkspaceId,
            AgentTemplateId = req.AgentTemplateId,
            ChannelId = "admin",
            OwnerUserId = userId,
            SessionType = SessionType.ServiceSession,
            Status = SessionStatus.Active,
            Title = title,
        };
        await _sessions.CreateAsync(session, ct);
        return CreatedAtAction(nameof(Get), new { sessionId = session.SessionId }, session);
    }

    /// <summary>POST /api/session/main — 确保 workspace 内某个 Agent 或 Group 的主线会话存在。</summary>
    [HttpPost("main")]
    public async Task<ActionResult<SessionRecord>> EnsureMain(
        [FromBody] EnsureMainSessionRequest req,
        CancellationToken ct)
    {
        var principalKind = req.PrincipalKind.Trim().ToLowerInvariant();
        if (principalKind is not "agent" and not "group")
            return BadRequest(new { message = "principalKind must be agent or group" });

        var existing = await _sessions.FindMainAsync(req.WorkspaceId, principalKind, req.PrincipalId, ct);
        if (existing is not null)
            return Ok(existing);

        var userId = User.Identity?.Name ?? "admin";
        var title = string.IsNullOrWhiteSpace(req.Title) ? "主线" : req.Title.Trim();
        var session = new SessionRecord
        {
            SessionId = Guid.NewGuid().ToString("N"),
            WorkspaceId = req.WorkspaceId,
            AgentTemplateId = req.AgentTemplateId,
            AgentInstanceId = principalKind == "agent" ? req.PrincipalId : null,
            ChannelId = "admin",
            OwnerUserId = userId,
            SessionType = SessionType.ServiceSession,
            SessionRole = SessionRole.Main,
            PrincipalKind = principalKind,
            PrincipalId = req.PrincipalId,
            Status = SessionStatus.Active,
            Title = title,
        };
        await _sessions.CreateAsync(session, ct);
        return Ok(session);
    }

    private async Task<string?> BuildDefaultTitleAsync(
        string workspaceId,
        string agentTemplateId,
        string? defaultTitleBase,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(defaultTitleBase))
            return null;

        var titleBase = defaultTitleBase.Trim();
        var sessions = await _sessions.QueryAsync(workspaceId: workspaceId, ct: ct);
        var maxSequence = sessions
            .Where(s => s.AgentTemplateId.Equals(agentTemplateId, StringComparison.OrdinalIgnoreCase))
            .Select(s => TryReadSequence(s.Title, titleBase, out var sequence) ? sequence : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{titleBase}{maxSequence + 1}";
    }

    private static bool TryReadSequence(string? title, string titleBase, out int sequence)
    {
        sequence = 0;
        var normalized = (title ?? string.Empty).Trim();
        if (normalized.Equals(titleBase, StringComparison.Ordinal))
        {
            sequence = 1;
            return true;
        }

        if (!normalized.StartsWith(titleBase, StringComparison.Ordinal))
            return false;

        var suffix = normalized[titleBase.Length..].Trim();
        return int.TryParse(suffix, out sequence) && sequence > 0;
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
    public string? DefaultTitleBase { get; init; }
}

public sealed record EnsureMainSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string PrincipalKind { get; init; }
    public required string PrincipalId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? Title { get; init; }
}
