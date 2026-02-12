using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using System.Text.Json;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Session 历史查询 JSON API（供 Admin SPA 调用）。</summary>
[ApiController]
[Route("api/sessions")]
public partial class SessionApiController : ControllerBase
{
    private readonly PlatformApiClient _api;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly SessionTitleService _sessionTitleService;
    private readonly WorkspaceAgentFileService _workspaceAgentFileService;
    private readonly SessionRedirectStore _redirectStore;
    private readonly ILogger<SessionApiController> _logger;

    public SessionApiController(
        PlatformApiClient api,
        IDbContextFactory<PlatformDbContext> dbFactory,
        SessionTitleService sessionTitleService,
        WorkspaceAgentFileService workspaceAgentFileService,
        SessionRedirectStore redirectStore,
        ILogger<SessionApiController> logger)
    {
        _api = api;
        _dbFactory = dbFactory;
        _sessionTitleService = sessionTitleService;
        _workspaceAgentFileService = workspaceAgentFileService;
        _redirectStore = redirectStore;
        _logger = logger;
    }

    /// <summary>GET /api/sessions?workspaceId=xxx</summary>
    [HttpGet]
    public async Task<ActionResult<List<SessionRecord>>> List(
        [FromQuery] string? workspaceId, CancellationToken ct)
    {
        var hotList = await _api.GetSessionsAsync(workspaceId, ct);
        var merged = await MergeTranscriptBackfillAsync(hotList, workspaceId, ct);
        return Ok(merged.Where(x => x.Status != SessionStatus.Frozen).ToList());
    }

    /// <summary>GET /api/sessions/{sessionId}</summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionRecord>> Get(string sessionId, CancellationToken ct)
    {
        var session = await _api.GetSessionAsync(sessionId, ct);
        session ??= await BuildTranscriptBackfillSessionAsync(sessionId, ct);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>DELETE /api/sessions/{sessionId} — 删除会话</summary>
    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken ct)
    {
        await _api.DeleteSessionAsync(sessionId, ct);
        await DeletePlatformSessionArtifactsAsync(sessionId, ct);
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
        var title = string.IsNullOrWhiteSpace(req.Title)
            ? await _sessionTitleService.BuildDefaultTitleAsync(
                req.WorkspaceId,
                req.AgentTemplateId,
                req.DefaultTitleBase,
                ct)
            : req.Title.Trim();
        var session = await _api.CreateSessionAsync(req.WorkspaceId, req.AgentTemplateId, title, ct: ct);
        return session is null
            ? BadRequest()
            : CreatedAtAction(nameof(Get), new { sessionId = session.SessionId }, session);
    }

    /// <summary>POST /api/sessions/main — 确保 Agent 或 Group 的主线会话存在。</summary>
    [Authorize]
    [HttpPost("main")]
    public async Task<ActionResult<SessionRecord>> EnsureMain(
        [FromBody] Services.EnsureMainSessionRequest req,
        CancellationToken ct)
    {
        var session = await _api.EnsureMainSessionAsync(req, ct);
        if (session is null)
            return BadRequest();

        var responseSession = session;
        if (string.Equals(req.PrincipalKind, "agent", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSessionId = _redirectStore.Resolve("main", req.WorkspaceId, req.PrincipalId);
            if (string.Equals(resolvedSessionId, "main", StringComparison.OrdinalIgnoreCase))
                resolvedSessionId = _redirectStore.Resolve(session.SessionId, req.WorkspaceId, req.PrincipalId);

            if (!string.Equals(resolvedSessionId, session.SessionId, StringComparison.Ordinal))
            {
                responseSession = await _api.GetSessionAsync(resolvedSessionId, ct)
                    ?? await BuildTranscriptBackfillSessionAsync(resolvedSessionId, ct)
                    ?? session with { SessionId = resolvedSessionId };
                _logger.LogInformation(
                    "Resolved agent main session redirect workspace={WorkspaceId} agent={AgentId} old={OldSessionId} new={NewSessionId}",
                    req.WorkspaceId,
                    req.PrincipalId,
                    session.SessionId,
                    resolvedSessionId);
            }

            try
            {
                await _workspaceAgentFileService.SetAgentMainSessionAsync(
                    req.WorkspaceId,
                    req.PrincipalId,
                    responseSession.SessionId,
                    ct);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to persist main session binding to workspace agent manifest: workspace={WorkspaceId} agent={AgentId} session={SessionId}",
                    req.WorkspaceId,
                    req.PrincipalId,
                    responseSession.SessionId);
            }
        }
        return Ok(responseSession);
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
    public string? DefaultTitleBase { get; init; }
}

public partial class SessionApiController
{
    private const string TranscriptBackfillChannelId = "admin-chat";
    private const string TranscriptBackfillOwnerUserId = "admin";
    private const string TranscriptBackfillAgentTemplateId = "global:general-assistant";

    private async Task<List<SessionRecord>> MergeTranscriptBackfillAsync(
        IReadOnlyCollection<SessionRecord> hotList,
        string? workspaceId,
        CancellationToken ct)
    {
        var merged = hotList.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        var backfill = await BuildTranscriptBackfillSessionsAsync(workspaceId, ct);
        foreach (var session in backfill)
            merged.TryAdd(session.SessionId, session);

        return merged.Values
            .OrderByDescending(s => s.LastActiveAt)
            .ToList();
    }

    private async Task<SessionRecord?> BuildTranscriptBackfillSessionAsync(string sessionId, CancellationToken ct)
    {
        var sessions = await BuildTranscriptBackfillSessionsAsync(null, ct);
        return sessions.FirstOrDefault(s => s.SessionId == sessionId);
    }

    private async Task<List<SessionRecord>> BuildTranscriptBackfillSessionsAsync(string? workspaceId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SessionEventLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(e => e.WorkspaceId == workspaceId);

        var facts = await query
            .GroupBy(e => new { e.SessionId, e.WorkspaceId })
            .Select(g => new
            {
                g.Key.SessionId,
                g.Key.WorkspaceId,
                FirstRecordedAt = g.Min(e => e.RecordedAt),
                LastRecordedAt = g.Max(e => e.RecordedAt),
            })
            .ToListAsync(ct);

        if (facts.Count == 0)
            return [];

        var sessionIds = facts.Select(f => f.SessionId).Distinct().ToList();
        var titleRows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => sessionIds.Contains(m.SessionId) && m.Role == "user")
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.SessionId, m.Content })
            .ToListAsync(ct);

        var titles = titleRows
            .GroupBy(m => m.SessionId)
            .ToDictionary(
                g => g.Key,
                g => BuildSessionTitle(g.First().Content),
                StringComparer.Ordinal);

        var metadataRows = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId) && e.EventType == "metadata")
            .OrderBy(e => e.SequenceNum)
            .Select(e => new { e.SessionId, e.Data })
            .ToListAsync(ct);

        var principalIds = metadataRows
            .GroupBy(e => e.SessionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => TryReadMetadataString(e.Data, "agent_id")
                        ?? TryReadMetadataString(e.Data, "source_id"))
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.Ordinal);

        var sessions = new List<SessionRecord>(facts.Count);
        foreach (var fact in facts)
        {
            var createdAt = ParseRecordedAt(fact.FirstRecordedAt);
            var lastActiveAt = ParseRecordedAt(fact.LastRecordedAt);
            var principalId = principalIds.GetValueOrDefault(fact.SessionId);
            sessions.Add(new SessionRecord
            {
                SessionId = fact.SessionId,
                WorkspaceId = fact.WorkspaceId,
                AgentTemplateId = TranscriptBackfillAgentTemplateId,
                ChannelId = TranscriptBackfillChannelId,
                OwnerUserId = TranscriptBackfillOwnerUserId,
                SessionType = SessionType.ServiceSession,
                Status = SessionStatus.Idle,
                PrincipalKind = string.IsNullOrWhiteSpace(principalId) ? null : "agent",
                PrincipalId = principalId,
                Title = titles.GetValueOrDefault(fact.SessionId) ?? "对话",
                CreatedAt = createdAt,
                LastActiveAt = lastActiveAt,
            });
        }

        _logger.LogDebug(
            "[SessionApi] Built transcript session backfill workspace={WorkspaceId} count={Count}",
            workspaceId ?? "*",
            sessions.Count);

        return sessions;
    }

    private static string? TryReadMetadataString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ParseRecordedAt(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static string BuildSessionTitle(string? content)
    {
        var normalized = (content ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return "对话";

        return normalized.Length <= 30
            ? normalized
            : normalized[..30];
    }

    private async Task DeletePlatformSessionArtifactsAsync(string sessionId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var deletedMessages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
        var deletedEvents = await db.SessionEventLogs
            .Where(e => e.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
        var deletedSubAgents = await db.SessionSubAgents
            .Where(s => s.ParentSessionId == sessionId || s.SubSessionId == sessionId)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "[SessionApi] Deleted session artifacts sessionId={SessionId} messages={Messages} events={Events} subAgents={SubAgents}",
            sessionId,
            deletedMessages,
            deletedEvents,
            deletedSubAgents);
    }
}
