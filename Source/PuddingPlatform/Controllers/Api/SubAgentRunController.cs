using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 子代理运行诊断 API — 查询运行列表、详情、事件、工具审计和输出。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
[ApiController]
[Route("api/sub-agents/runs")]
[Authorize]
public class SubAgentRunController : ControllerBase
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ISubAgentRunStore _runStore;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SubAgentRunController(
        IDbContextFactory<PlatformDbContext> dbFactory,
        ISubAgentRunStore runStore)
    {
        _dbFactory = dbFactory;
        _runStore = runStore;
    }

    /// <summary>
    /// GET /api/sub-agents/runs — 分页查询子代理运行列表。
    /// 支持按 parentSessionId、workspaceId、agentInstanceId、status 过滤。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] string? parentSessionId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? agentInstanceId,
        [FromQuery] string? status,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.SubAgentRuns.AsQueryable();

        if (!string.IsNullOrWhiteSpace(parentSessionId))
            query = query.Where(r => r.ParentSessionId == parentSessionId);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(r => r.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(r => r.AgentInstanceId == agentInstanceId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var total = await query.CountAsync(ct);

        var runs = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(new { runs, total });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId} — 获取单次运行的 Manifest 详情。
    /// </summary>
    [HttpGet("{runId}")]
    public async Task<ActionResult<SubAgentRunManifest>> Get(string runId, CancellationToken ct)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        return Ok(archive.Manifest);
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/events — 获取运行事件列表（从 events.jsonl 读取）。
    /// 支持 offset/limit 分页。
    /// </summary>
    [HttpGet("{runId}/events")]
    public async Task<ActionResult<object>> Events(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        var allEvents = archive.Events;
        var total = allEvents.Count;
        var paged = allEvents.Skip(offset).Take(limit).ToList();

        return Ok(new { events = paged, total });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/tools — 获取工具审计列表（从 tools.jsonl 读取）。
    /// 支持 offset/limit 分页。
    /// </summary>
    [HttpGet("{runId}/tools")]
    public async Task<ActionResult<object>> Tools(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        var allTools = archive.Tools;
        var total = allTools.Count;
        var paged = allTools.Skip(offset).Take(limit).ToList();

        return Ok(new { tools = paged, total });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/output — 获取最终输出内容（output.md 内容）。
    /// </summary>
    [HttpGet("{runId}/output")]
    public async Task<ActionResult<object>> Output(string runId, CancellationToken ct)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        return Ok(new { output = archive.Output });
    }
}
