using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的渠道管道管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/channels")]
public class WorkspaceChannelApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/channels
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceChannelDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var list = await db.WorkspaceChannels
            .AsNoTracking()
            .Where(c => c.WorkspaceEntityId == ws.Id)
            .OrderBy(c => c.Id)
            .Select(c => ToDto(c))
            .ToListAsync(ct);

        return Ok(list);
    }

    // GET /api/workspaces/{workspaceId}/channels/{channelId}
    [HttpGet("{channelId}")]
    public async Task<ActionResult<WorkspaceChannelDto>> Get(string workspaceId, string channelId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var channel = await db.WorkspaceChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.WorkspaceEntityId == ws.Id && c.ChannelId == channelId, ct);

        return channel is null ? NotFound() : Ok(ToDto(channel));
    }

    // POST /api/workspaces/{workspaceId}/channels
    [HttpPost]
    public async Task<ActionResult<WorkspaceChannelDto>> Create(
        string workspaceId, [FromBody] UpsertWorkspaceChannelRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = new WorkspaceChannelEntity
        {
            ChannelId         = Guid.NewGuid().ToString(),
            WorkspaceEntityId = ws.Id,
            Name              = req.Name,
            Description       = req.Description,
            ChannelType       = req.ChannelType,
            DefaultAgentId    = req.DefaultAgentId,
            ConfigJson        = req.ConfigJson,
            IsEnabled         = req.IsEnabled,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow,
        };
        db.WorkspaceChannels.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, channelId = entity.ChannelId }, ToDto(entity));
    }

    // PUT /api/workspaces/{workspaceId}/channels/{channelId}
    [HttpPut("{channelId}")]
    public async Task<ActionResult<WorkspaceChannelDto>> Update(
        string workspaceId, string channelId,
        [FromBody] UpsertWorkspaceChannelRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var channel = await db.WorkspaceChannels
            .FirstOrDefaultAsync(c => c.WorkspaceEntityId == ws.Id && c.ChannelId == channelId, ct);
        if (channel is null) return NotFound();

        channel.Name           = req.Name;
        channel.Description    = req.Description;
        channel.ChannelType    = req.ChannelType;
        channel.DefaultAgentId = req.DefaultAgentId;
        channel.ConfigJson     = req.ConfigJson;
        channel.IsEnabled      = req.IsEnabled;
        channel.UpdatedAt      = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(channel));
    }

    // DELETE /api/workspaces/{workspaceId}/channels/{channelId}
    [HttpDelete("{channelId}")]
    public async Task<IActionResult> Delete(string workspaceId, string channelId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var channel = await db.WorkspaceChannels
            .FirstOrDefaultAsync(c => c.WorkspaceEntityId == ws.Id && c.ChannelId == channelId, ct);
        if (channel is null) return NotFound();

        db.WorkspaceChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkspaceChannelDto ToDto(WorkspaceChannelEntity e) => new(
        e.ChannelId, e.Name, e.Description, e.ChannelType,
        e.DefaultAgentId, e.ConfigJson, e.IsEnabled, e.CreatedAt, e.UpdatedAt);
}
