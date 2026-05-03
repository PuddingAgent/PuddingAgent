using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的 Agent 实例管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/agents")]
public class WorkspaceAgentApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/agents
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceAgentDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var list = await db.WorkspaceAgents
            .AsNoTracking()
            .Where(a => a.WorkspaceEntityId == ws.Id)
            .OrderBy(a => a.Id)
            .Select(a => ToDto(a))
            .ToListAsync(ct);

        return Ok(list);
    }

    // GET /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpGet("{agentId}")]
    public async Task<ActionResult<WorkspaceAgentDto>> Get(string workspaceId, string agentId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agent = await db.WorkspaceAgents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.WorkspaceEntityId == ws.Id && a.AgentId == agentId, ct);

        return agent is null ? NotFound() : Ok(ToDto(agent));
    }

    // POST /api/workspaces/{workspaceId}/agents
    [HttpPost]
    public async Task<ActionResult<WorkspaceAgentDto>> Create(
        string workspaceId, [FromBody] CreateWorkspaceAgentRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = new WorkspaceAgentEntity
        {
            AgentId              = Guid.NewGuid().ToString(),
            WorkspaceEntityId    = ws.Id,
            Name                 = req.Name,
            Description          = req.Description,
            DisplayName          = req.DisplayName,
            AvatarUrl            = req.AvatarUrl,
            SourceTemplateId     = req.SourceTemplateId,
            SystemPromptOverride = req.SystemPromptOverride,
            PreferredProviderId  = req.PreferredProviderId,
            PreferredModelId     = req.PreferredModelId,
            IsEnabled            = true,
            IsFrozen             = false,
            CreatedAt            = DateTimeOffset.UtcNow,
            UpdatedAt            = DateTimeOffset.UtcNow,
        };
        db.WorkspaceAgents.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, agentId = entity.AgentId }, ToDto(entity));
    }

    // PUT /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpPut("{agentId}")]
    public async Task<ActionResult<WorkspaceAgentDto>> Update(
        string workspaceId, string agentId,
        [FromBody] UpdateWorkspaceAgentRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agent = await db.WorkspaceAgents
            .FirstOrDefaultAsync(a => a.WorkspaceEntityId == ws.Id && a.AgentId == agentId, ct);
        if (agent is null) return NotFound();

        agent.Name                 = req.Name;
        agent.Description          = req.Description;
        agent.DisplayName          = req.DisplayName;
        agent.AvatarUrl            = req.AvatarUrl;
        agent.SourceTemplateId     = req.SourceTemplateId;
        agent.SystemPromptOverride = req.SystemPromptOverride;
        agent.PreferredProviderId  = req.PreferredProviderId;
        agent.PreferredModelId     = req.PreferredModelId;
        agent.IsEnabled            = req.IsEnabled;
        agent.UpdatedAt            = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(agent));
    }

    // POST /api/workspaces/{workspaceId}/agents/{agentId}/freeze
    [HttpPost("{agentId}/freeze")]
    public async Task<IActionResult> Freeze(string workspaceId, string agentId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agent = await db.WorkspaceAgents
            .FirstOrDefaultAsync(a => a.WorkspaceEntityId == ws.Id && a.AgentId == agentId, ct);
        if (agent is null) return NotFound();

        agent.IsFrozen  = true;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    // POST /api/workspaces/{workspaceId}/agents/{agentId}/unfreeze
    [HttpPost("{agentId}/unfreeze")]
    public async Task<IActionResult> Unfreeze(string workspaceId, string agentId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agent = await db.WorkspaceAgents
            .FirstOrDefaultAsync(a => a.WorkspaceEntityId == ws.Id && a.AgentId == agentId, ct);
        if (agent is null) return NotFound();

        agent.IsFrozen  = false;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    // DELETE /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpDelete("{agentId}")]
    public async Task<IActionResult> Delete(string workspaceId, string agentId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agent = await db.WorkspaceAgents
            .FirstOrDefaultAsync(a => a.WorkspaceEntityId == ws.Id && a.AgentId == agentId, ct);
        if (agent is null) return NotFound();

        db.WorkspaceAgents.Remove(agent);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkspaceAgentDto ToDto(WorkspaceAgentEntity e) => new(
        e.AgentId, e.Name, e.Description,
        e.DisplayName, e.AvatarUrl,
        e.SourceTemplateId,
        e.SystemPromptOverride, e.PreferredProviderId, e.PreferredModelId,
        e.IsEnabled, e.IsFrozen, e.CreatedAt, e.UpdatedAt);
}
