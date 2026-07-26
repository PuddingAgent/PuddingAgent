using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的技能/工具管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/skills")]
public class WorkspaceSkillApiController(
    PlatformDbContext db,
    IMcpConnectionManager? mcpConnectionManager = null) : ControllerBase
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
        if (!TryNormalizeRequest(req, out var skillType, out var configJson, out var validationError))
            return BadRequest(new { message = validationError });

        var entity = new WorkspaceSkillEntity
        {
            SkillId           = Guid.NewGuid().ToString(),
            WorkspaceEntityId = ws.Id,
            Name              = req.Name,
            Description       = req.Description,
            SkillType         = skillType,
            ConfigJson        = configJson,
            IsEnabled         = req.IsEnabled,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow,
        };
        db.WorkspaceSkills.Add(entity);
        await db.SaveChangesAsync(ct);
        if (IsMcp(entity.SkillType) && mcpConnectionManager is not null)
            await mcpConnectionManager.RefreshWorkspaceAsync(workspaceId, ct);

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
        if (!TryNormalizeRequest(req, out var skillType, out var configJson, out var validationError))
            return BadRequest(new { message = validationError });

        var wasMcp = IsMcp(skill.SkillType);
        skill.Name        = req.Name;
        skill.Description = req.Description;
        skill.SkillType   = skillType;
        skill.ConfigJson  = configJson;
        skill.IsEnabled   = req.IsEnabled;
        skill.UpdatedAt   = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        if ((wasMcp || IsMcp(skill.SkillType)) && mcpConnectionManager is not null)
            await mcpConnectionManager.RefreshWorkspaceAsync(workspaceId, ct);
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

        var wasMcp = IsMcp(skill.SkillType);
        db.WorkspaceSkills.Remove(skill);
        await db.SaveChangesAsync(ct);
        if (wasMcp && mcpConnectionManager is not null)
            await mcpConnectionManager.RefreshWorkspaceAsync(workspaceId, ct);
        return NoContent();
    }

    [HttpGet("{skillId}/runtime-status")]
    public async Task<ActionResult<McpServerRuntimeStatus>> GetRuntimeStatus(
        string workspaceId,
        string skillId,
        CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });
        var exists = await db.WorkspaceSkills.AsNoTracking()
            .AnyAsync(skill => skill.WorkspaceEntityId == ws.Id && skill.SkillId == skillId, ct);
        if (!exists || mcpConnectionManager is null) return NotFound();

        var status = mcpConnectionManager.ListStatuses(workspaceId)
            .FirstOrDefault(item => item.SkillId == skillId);
        return status is null ? NotFound() : Ok(status);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkspaceSkillDto ToDto(WorkspaceSkillEntity e) => new(
        e.SkillId, e.Name, e.Description, e.SkillType,
        e.ConfigJson, e.IsEnabled, e.CreatedAt, e.UpdatedAt);

    private static bool TryNormalizeRequest(
        UpsertWorkspaceSkillRequest req,
        out string skillType,
        out string? configJson,
        out string? error)
    {
        skillType = req.SkillType?.Trim() ?? string.Empty;
        configJson = req.ConfigJson;
        error = null;
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            error = "Skill name is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(skillType))
        {
            error = "skillType is required.";
            return false;
        }

        if (!IsMcp(skillType))
            return true;

        skillType = "MCP";
        if (!McpServerConfig.TryParse(req.ConfigJson, out var config, out error))
            return false;
        configJson = config!.ToCanonicalJson();
        return true;
    }

    private static bool IsMcp(string? skillType) =>
        string.Equals(skillType, "MCP", StringComparison.OrdinalIgnoreCase);
}
