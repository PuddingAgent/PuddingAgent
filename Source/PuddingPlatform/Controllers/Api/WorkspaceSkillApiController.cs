using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的技能/工具管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/skills")]
public class WorkspaceSkillApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/skills
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceSkillDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var list = await db.WorkspaceSkills
            .AsNoTracking()
            .Where(s => s.WorkspaceEntityId == ws.Id)
            .OrderBy(s => s.Id)
            .Select(s => ToDto(s))
            .ToListAsync(ct);

        return Ok(list);
    }

    // GET /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpGet("{skillId}")]
    public async Task<ActionResult<WorkspaceSkillDto>> Get(string workspaceId, string skillId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var skill = await db.WorkspaceSkills
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceEntityId == ws.Id && s.SkillId == skillId, ct);

        return skill is null ? NotFound() : Ok(ToDto(skill));
    }

    // POST /api/workspaces/{workspaceId}/skills
    [HttpPost]
    public async Task<ActionResult<WorkspaceSkillDto>> Create(
        string workspaceId, [FromBody] UpsertWorkspaceSkillRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = new WorkspaceSkillEntity
        {
            SkillId           = Guid.NewGuid().ToString(),
            WorkspaceEntityId = ws.Id,
            Name              = req.Name,
            Description       = req.Description,
            SkillType         = req.SkillType,
            ConfigJson        = req.ConfigJson,
            IsEnabled         = req.IsEnabled,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow,
        };
        db.WorkspaceSkills.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, skillId = entity.SkillId }, ToDto(entity));
    }

    // PUT /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpPut("{skillId}")]
    public async Task<ActionResult<WorkspaceSkillDto>> Update(
        string workspaceId, string skillId,
        [FromBody] UpsertWorkspaceSkillRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var skill = await db.WorkspaceSkills
            .FirstOrDefaultAsync(s => s.WorkspaceEntityId == ws.Id && s.SkillId == skillId, ct);
        if (skill is null) return NotFound();

        skill.Name        = req.Name;
        skill.Description = req.Description;
        skill.SkillType   = req.SkillType;
        skill.ConfigJson  = req.ConfigJson;
        skill.IsEnabled   = req.IsEnabled;
        skill.UpdatedAt   = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(skill));
    }

    // DELETE /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpDelete("{skillId}")]
    public async Task<IActionResult> Delete(string workspaceId, string skillId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var skill = await db.WorkspaceSkills
            .FirstOrDefaultAsync(s => s.WorkspaceEntityId == ws.Id && s.SkillId == skillId, ct);
        if (skill is null) return NotFound();

        db.WorkspaceSkills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkspaceSkillDto ToDto(WorkspaceSkillEntity e) => new(
        e.SkillId, e.Name, e.Description, e.SkillType,
        e.ConfigJson, e.IsEnabled, e.CreatedAt, e.UpdatedAt);
}
