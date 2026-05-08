using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
        entity.PersonaPrompt = req.PersonaPrompt;
        entity.ToolsDescription = req.ToolsDescription;
        entity.BootstrapTemplate = req.BootstrapTemplate;
        entity.AvatarEmoji = req.AvatarEmoji;
        entity.PreferredProviderId = req.PreferredProviderId;
        entity.PreferredModelId = req.PreferredModelId;
        entity.MemoryLlmEndpoint = req.MemoryLlmEndpoint;
        entity.MemoryLlmApiKey = req.MemoryLlmApiKey;
        entity.MemoryLlmModelId = req.MemoryLlmModelId;
        entity.MemorySearchMode = req.MemorySearchMode ?? "deep";
        entity.ReasoningEffort = req.ReasoningEffort;
        entity.MaxContextTokens = req.MaxContextTokens;
        entity.MaxReplyTokens = req.MaxReplyTokens;
        entity.ContainerImage = req.ContainerImage;
        entity.SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []);
        entity.SelectedSkillPackageIdsJson = JsonSerializer.Serialize(req.SelectedSkillPackageIds ?? []);
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
        PersonaPrompt = req.PersonaPrompt,
        ToolsDescription = req.ToolsDescription,
        BootstrapTemplate = req.BootstrapTemplate,
        AvatarEmoji = req.AvatarEmoji,
        PreferredProviderId = req.PreferredProviderId,
        PreferredModelId = req.PreferredModelId,
        MemoryLlmEndpoint = req.MemoryLlmEndpoint,
        MemoryLlmApiKey = req.MemoryLlmApiKey,
        MemoryLlmModelId = req.MemoryLlmModelId,
        MemorySearchMode = req.MemorySearchMode ?? "deep",
        ReasoningEffort = req.ReasoningEffort,
        MaxContextTokens = req.MaxContextTokens,
        MaxReplyTokens = req.MaxReplyTokens,
        ContainerImage = req.ContainerImage,
        SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []),
        SelectedSkillPackageIdsJson = JsonSerializer.Serialize(req.SelectedSkillPackageIds ?? []),
        IsBuiltIn = isBuiltIn,
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
    };

    private static GlobalAgentTemplateDto MapToDto(GlobalAgentTemplateEntity t) => new(
        t.Id, t.TemplateId, t.Name, t.Description, t.Role,
        t.SystemPrompt, t.UserPromptTemplate,
        t.PreferredProviderId, t.PreferredModelId,
        t.MaxContextTokens, t.MaxReplyTokens,
        t.ContainerImage, ParseStringList(t.SelectedCapabilityIdsJson),
        ParseStringList(t.SelectedSkillPackageIdsJson),
        t.IsBuiltIn, t.IsEnabled, t.SortOrder,
        t.CreatedAt, t.UpdatedAt,
        t.PersonaPrompt, t.ToolsDescription, t.BootstrapTemplate, t.AvatarEmoji,
        t.MemoryLlmEndpoint, t.MemoryLlmApiKey, t.MemoryLlmModelId, t.MemorySearchMode,
        t.ReasoningEffort);

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
