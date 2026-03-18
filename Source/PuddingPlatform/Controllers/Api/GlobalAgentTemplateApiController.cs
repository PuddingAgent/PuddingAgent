using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>全局系统内置 Agent 模板管理 API</summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(PlatformDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var query = db.GlobalAgentTemplates.AsNoTracking();
        if (enabledOnly == true) query = query.Where(t => t.IsEnabled);

        var list = await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        return Ok(list.Select(MapToDto).ToList());
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Get(string templateId, CancellationToken ct)
    {
        var t = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TemplateId == templateId, ct);
        return t is null ? NotFound() : Ok(MapToDto(t));
    }

    [HttpPost]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Create(
        [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        if (await db.GlobalAgentTemplates.AnyAsync(t => t.TemplateId == req.TemplateId, ct))
            return Conflict(new { error = $"TemplateId '{req.TemplateId}' 已存在" });

        var entity = BuildEntity(req, isBuiltIn: false);
        db.GlobalAgentTemplates.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { templateId = entity.TemplateId }, MapToDto(entity));
    }

    [HttpPut("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Update(
        string templateId, [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates.FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();

        entity.Name = req.Name;
        entity.Description = req.Description;
        entity.Role = req.Role;
        entity.SystemPrompt = req.SystemPrompt;
        entity.UserPromptTemplate = req.UserPromptTemplate;
        entity.PreferredProviderId = req.PreferredProviderId;
        entity.PreferredModelId = req.PreferredModelId;
        entity.MaxContextTokens = req.MaxContextTokens;
        entity.MaxReplyTokens = req.MaxReplyTokens;
        entity.IsEnabled = req.IsEnabled;
        entity.SortOrder = req.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(entity));
    }

    [HttpDelete("{templateId}")]
    public async Task<IActionResult> Delete(string templateId, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates.FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();
        if (entity.IsBuiltIn) return BadRequest(new { error = "系统内置模板不允许删除" });

        db.GlobalAgentTemplates.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static GlobalAgentTemplateEntity BuildEntity(
        UpsertGlobalAgentTemplateRequest req, bool isBuiltIn) => new()
    {
        TemplateId = req.TemplateId,
        Name = req.Name,
        Description = req.Description,
        Role = req.Role,
        SystemPrompt = req.SystemPrompt,
        UserPromptTemplate = req.UserPromptTemplate,
        PreferredProviderId = req.PreferredProviderId,
        PreferredModelId = req.PreferredModelId,
        MaxContextTokens = req.MaxContextTokens,
        MaxReplyTokens = req.MaxReplyTokens,
        IsBuiltIn = isBuiltIn,
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
    };

    private static GlobalAgentTemplateDto MapToDto(GlobalAgentTemplateEntity t) => new(
        t.Id, t.TemplateId, t.Name, t.Description, t.Role,
        t.SystemPrompt, t.UserPromptTemplate,
        t.PreferredProviderId, t.PreferredModelId,
        t.MaxContextTokens, t.MaxReplyTokens,
        t.IsBuiltIn, t.IsEnabled, t.SortOrder,
        t.CreatedAt, t.UpdatedAt);
}
