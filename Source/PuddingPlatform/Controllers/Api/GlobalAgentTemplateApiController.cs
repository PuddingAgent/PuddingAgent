using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>全局系统内置 Agent 模板管理 API</summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(
    PlatformDbContext db,
    AgentAvatarCatalog avatarCatalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var query = db.GlobalAgentTemplates.AsNoTracking();
        if (enabledOnly == true) query = query.Where(t => t.IsEnabled);

        var list = await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(list.Select(t => MapToDto(t, avatarMap)).ToList());
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Get(string templateId, CancellationToken ct)
    {
        var t = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TemplateId == templateId, ct);
        if (t is null) return NotFound();
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(MapToDto(t, avatarMap));
    }

    [HttpPost]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Create(
        [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        if (await db.GlobalAgentTemplates.AnyAsync(t => t.TemplateId == req.TemplateId, ct))
            return Conflict(new { error = $"TemplateId '{req.TemplateId}' 已存在" });

        // ADR-034：校验或补全 AvatarId
        var resolvedAvatarId = await ResolveAvatarIdAsync(req.AvatarId);
        if (resolvedAvatarId is null)
            return BadRequest(new { error = "没有可用的系统头像，请先配置头像资源" });

        var entity = BuildEntity(req, isBuiltIn: false);
        entity.AvatarId = resolvedAvatarId;
        db.GlobalAgentTemplates.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { templateId = entity.TemplateId }, await MapToDtoAsync(entity));
    }

    [HttpPut("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Update(
        string templateId, [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates.FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();

        // ADR-034：校验或补全 AvatarId
        var resolvedAvatarId = await ResolveAvatarIdAsync(req.AvatarId);
        if (resolvedAvatarId is null)
            return BadRequest(new { error = "没有可用的系统头像，请先配置头像资源" });
        entity.AvatarId = resolvedAvatarId;

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
        entity.MemoryLlmProviderId = req.MemoryLlmProviderId;
        entity.MemoryLlmModelId = req.MemoryLlmModelId;
        entity.MemorySearchMode = req.MemorySearchMode ?? "deep";
        entity.ReasoningEffort = req.ReasoningEffort;
        entity.MaxRounds = req.MaxRounds ?? 200;
        entity.MaxElapsedSeconds = req.MaxElapsedSeconds ?? 1200;
        entity.MaxToolCallsTotal = req.MaxToolCallsTotal ?? 100;
        entity.MaxContextTokens = req.MaxContextTokens;
        entity.MaxReplyTokens = req.MaxReplyTokens;
        entity.ContainerImage = req.ContainerImage;
        entity.SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []);
        entity.SelectedSkillPackageIdsJson = JsonSerializer.Serialize(req.SelectedSkillPackageIds ?? []);
        entity.IsEnabled = req.IsEnabled;
        entity.SortOrder = req.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(MapToDto(entity, avatarMap));
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
        MemoryLlmProviderId = req.MemoryLlmProviderId,
        MemoryLlmModelId = req.MemoryLlmModelId,
        MemorySearchMode = req.MemorySearchMode ?? "deep",
        ReasoningEffort = req.ReasoningEffort,
        MaxRounds = req.MaxRounds ?? 200,
        MaxElapsedSeconds = req.MaxElapsedSeconds ?? 1200,
        MaxToolCallsTotal = req.MaxToolCallsTotal ?? 100,
        MaxContextTokens = req.MaxContextTokens,
        MaxReplyTokens = req.MaxReplyTokens,
        ContainerImage = req.ContainerImage,
        SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []),
        SelectedSkillPackageIdsJson = JsonSerializer.Serialize(req.SelectedSkillPackageIds ?? []),
        IsBuiltIn = isBuiltIn,
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
    };

    private GlobalAgentTemplateDto MapToDto(GlobalAgentTemplateEntity t, Dictionary<string, AgentAvatarEntity>? avatarMap = null)
    {
        var avatarId = t.AvatarId;
        AgentAvatarEntity? avatar = null;
        if (!string.IsNullOrWhiteSpace(avatarId) && avatarMap?.TryGetValue(avatarId, out avatar) == true)
        {
            // 已找到对应头像
        }
        else if (avatarMap is not null)
        {
            // 尝试用默认头像
            avatar = avatarMap.Values.MinBy(a => (a.SortOrder, a.Id));
        }

        return new(
            t.Id, t.TemplateId, t.Name, t.Description, t.Role,
            t.SystemPrompt, t.UserPromptTemplate,
            t.PreferredProviderId, t.PreferredModelId,
            t.MaxContextTokens, t.MaxReplyTokens,
            t.ContainerImage, ParseStringList(t.SelectedCapabilityIdsJson),
            ParseStringList(t.SelectedSkillPackageIdsJson),
            t.IsBuiltIn, t.IsEnabled, t.SortOrder,
            t.CreatedAt, t.UpdatedAt,
            t.PersonaPrompt, t.ToolsDescription, t.BootstrapTemplate, t.AvatarEmoji,
            t.AvatarId,
            avatar?.UrlPath,
            avatar?.Name,
            t.MemoryLlmProviderId, t.MemoryLlmModelId, t.MemorySearchMode,
            t.ReasoningEffort,
            t.MaxRounds, t.MaxElapsedSeconds, t.MaxToolCallsTotal);
    }

    private async Task<string?> ResolveAvatarIdAsync(string? avatarId)
    {
        // 如果传入了有效 ID，校验存在且启用
        if (!string.IsNullOrWhiteSpace(avatarId))
        {
            var avatar = await avatarCatalog.GetRequiredEnabledAsync(avatarId);
            if (avatar is not null) return avatarId;
            // 不存在或已禁用，返回 null 让调用方返回 400
            return null;
        }

        // 未传入时使用默认头像
        var defaultAvatar = await avatarCatalog.GetDefaultAsync();
        return defaultAvatar?.AvatarId;
    }

    private async Task<GlobalAgentTemplateDto> MapToDtoAsync(GlobalAgentTemplateEntity t)
    {
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return MapToDto(t, avatarMap);
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
