using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 全局系统 Agent 模板管理 API（DB 主源）。启动时从 default-data/agent-templates/ 种子导入。
/// </summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(PlatformDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var query = db.GlobalAgentTemplates.AsNoTracking();
        if (enabledOnly == true)
            query = query.Where(t => t.IsEnabled);
        var list = await query.OrderBy(t => t.SortOrder).ToListAsync(ct);
        var avatarsMap = await GetEnabledAvatarMapAsync(ct);
        return Ok(list.Select(e => MapToDto(e, avatarsMap)).ToList());
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Get(string templateId, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();
        var avatarsMap = await GetEnabledAvatarMapAsync(ct);
        return Ok(MapToDto(entity, avatarsMap));
    }

    [HttpPost]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Create(
        [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        if (await db.GlobalAgentTemplates.AnyAsync(t => t.TemplateId == req.TemplateId, ct))
            return Conflict(new { error = $"Template '{req.TemplateId}' already exists" });

        var entity = MapToEntity(req);
        db.GlobalAgentTemplates.Add(entity);
        await db.SaveChangesAsync(ct);
        var avatarsMap = await GetEnabledAvatarMapAsync(ct);
        return CreatedAtAction(nameof(Get), new { templateId = entity.TemplateId }, MapToDto(entity, avatarsMap));
    }

    [HttpPut("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Update(
        string templateId, [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();

        ApplyUpdate(entity, req);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var avatarsMap = await GetEnabledAvatarMapAsync(ct);
        return Ok(MapToDto(entity, avatarsMap));
    }

    [HttpDelete("{templateId}")]
    public async Task<IActionResult> Delete(string templateId, CancellationToken ct)
    {
        var entity = await db.GlobalAgentTemplates
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, ct);
        if (entity is null) return NotFound();
        if (entity.IsBuiltIn)
            return BadRequest(new { error = "Built-in templates cannot be deleted" });

        db.GlobalAgentTemplates.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Entity ↔ DTO mapping ─────────────────────────────────────

    private static List<string> ParseJsonArray(string? json) =>
        string.IsNullOrEmpty(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string ToJsonArray(List<string>? list) =>
        list is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(list) : "[]";

    private static GlobalAgentTemplateDto MapToDto(GlobalAgentTemplateEntity e, Dictionary<string, AgentAvatarEntity>? avatarsMap = null)
    {
        // 解析头像 URL：从 avatarsMap 查询，无匹配或未传时返回 null
        string? avatarUrl = null;
        if (!string.IsNullOrWhiteSpace(e.AvatarId) && avatarsMap?.TryGetValue(e.AvatarId, out var avatar) == true)
            avatarUrl = avatar.UrlPath;

        return new(
            Id: e.Id,
            TemplateId: e.TemplateId,
            Name: e.Name,
            Description: e.Description,
            Role: e.Role,
            SystemPrompt: e.SystemPrompt,
            UserPromptTemplate: e.UserPromptTemplate,
            PreferredProviderId: e.PreferredProviderId,
            PreferredModelId: e.PreferredModelId,
            MaxContextTokens: e.MaxContextTokens,
            MaxReplyTokens: e.MaxReplyTokens,
            ContainerImage: e.ContainerImage,
            SelectedCapabilityIds: ParseJsonArray(e.SelectedCapabilityIdsJson),
            SelectedSkillPackageIds: ParseJsonArray(e.SelectedSkillPackageIdsJson),
            IsBuiltIn: e.IsBuiltIn,
            IsEnabled: e.IsEnabled,
            SortOrder: e.SortOrder,
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            PersonaPrompt: e.PersonaPrompt,
            ToolsDescription: e.ToolsDescription,
            BootstrapTemplate: e.BootstrapTemplate,
            AgentsPrompt: e.AgentsPrompt,
            MemoryPrompt: e.MemoryPrompt,
            AvatarEmoji: e.AvatarEmoji,
            AvatarId: e.AvatarId,
            AvatarUrl: avatarUrl,
            MemoryLlmProviderId: e.MemoryLlmProviderId,
            MemoryLlmModelId: e.MemoryLlmModelId,
            MemorySearchMode: e.MemorySearchMode,
            ReasoningEffort: e.ReasoningEffort,
            MaxRounds: e.MaxRounds,
            MaxElapsedSeconds: e.MaxElapsedSeconds,
            MaxToolCallsTotal: e.MaxToolCallsTotal,
            ConsciousProfileId: e.PreferredProviderId,
            SubconsciousProfileId: e.MemoryLlmProviderId
        );
    }

    private async Task<Dictionary<string, AgentAvatarEntity>> GetEnabledAvatarMapAsync(CancellationToken ct)
    {
        return await db.AgentAvatars
            .AsNoTracking()
            .Where(a => a.IsEnabled)
            .ToDictionaryAsync(a => a.AvatarId, a => a, ct);
    }

    private static GlobalAgentTemplateEntity MapToEntity(UpsertGlobalAgentTemplateRequest req) => new()
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
        AgentsPrompt = req.AgentsPrompt,
        MemoryPrompt = req.MemoryPrompt,
        AvatarEmoji = req.AvatarEmoji,
        AvatarId = req.AvatarId,
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
        SelectedCapabilityIdsJson = ToJsonArray(req.SelectedCapabilityIds),
        SelectedSkillPackageIdsJson = ToJsonArray(req.SelectedSkillPackageIds),
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static void ApplyUpdate(GlobalAgentTemplateEntity e, UpsertGlobalAgentTemplateRequest req)
    {
        e.Name = req.Name;
        e.Description = req.Description;
        e.Role = req.Role;
        e.SystemPrompt = req.SystemPrompt;
        e.UserPromptTemplate = req.UserPromptTemplate;
        e.PersonaPrompt = req.PersonaPrompt;
        e.ToolsDescription = req.ToolsDescription;
        e.BootstrapTemplate = req.BootstrapTemplate;
        e.AgentsPrompt = req.AgentsPrompt;
        e.MemoryPrompt = req.MemoryPrompt;
        e.AvatarEmoji = req.AvatarEmoji;
        e.AvatarId = req.AvatarId;
        e.PreferredProviderId = req.PreferredProviderId;
        e.PreferredModelId = req.PreferredModelId;
        e.MemoryLlmProviderId = req.MemoryLlmProviderId;
        e.MemoryLlmModelId = req.MemoryLlmModelId;
        e.MemorySearchMode = req.MemorySearchMode ?? "deep";
        e.ReasoningEffort = req.ReasoningEffort;
        if (req.MaxRounds.HasValue) e.MaxRounds = req.MaxRounds.Value;
        if (req.MaxElapsedSeconds.HasValue) e.MaxElapsedSeconds = req.MaxElapsedSeconds.Value;
        if (req.MaxToolCallsTotal.HasValue) e.MaxToolCallsTotal = req.MaxToolCallsTotal.Value;
        e.MaxContextTokens = req.MaxContextTokens;
        e.MaxReplyTokens = req.MaxReplyTokens;
        e.ContainerImage = req.ContainerImage;
        e.SelectedCapabilityIdsJson = ToJsonArray(req.SelectedCapabilityIds);
        e.SelectedSkillPackageIdsJson = ToJsonArray(req.SelectedSkillPackageIds);
        e.IsEnabled = req.IsEnabled;
        e.SortOrder = req.SortOrder;
    }
}
