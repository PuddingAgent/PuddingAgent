using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Workspace 级 Agent 模板管理 API</summary>
[ApiController]
[Route("api/workspace-agent-templates")]
public class WorkspaceAgentTemplateApiController(
    PlatformDbContext db,
    AgentAvatarCatalog avatarCatalog) : ControllerBase
{
    // ── 查询某 Workspace 的所有模板 ───────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceAgentTemplateDto>>> List(
        [FromQuery] string? workspaceId,
        [FromQuery] bool? enabledOnly,
        CancellationToken ct)
    {
        var query = db.WorkspaceAgentTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(t => t.WorkspaceId == workspaceId);
        if (enabledOnly == true)
            query = query.Where(t => t.IsEnabled);

        var list = await query.OrderBy(t => t.WorkspaceId).ThenBy(t => t.SortOrder).ToListAsync(ct);
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(list.Select(t => MapToDto(t, avatarMap)).ToList());
    }

    // ── 查询单个模板 ──────────────────────────────────────────────
    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkspaceAgentTemplateDto>> GetById(int id, CancellationToken ct)
    {
        var t = await db.WorkspaceAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(MapToDto(t, avatarMap));
    }

    // ── 按 workspaceId + templateId 精确查询 ─────────────────────
    [HttpGet("{workspaceId}/{templateId}")]
    public async Task<ActionResult<WorkspaceAgentTemplateDto>> GetByKey(
        string workspaceId, string templateId, CancellationToken ct)
    {
        var t = await db.WorkspaceAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.TemplateId == templateId, ct);
        if (t is null) return NotFound();
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(MapToDto(t, avatarMap));
    }

    // ── 创建 ──────────────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<WorkspaceAgentTemplateDto>> Create(
        [FromBody] UpsertWorkspaceAgentTemplateRequest req, CancellationToken ct)
    {
        if (await db.WorkspaceAgentTemplates.AnyAsync(
                t => t.WorkspaceId == req.WorkspaceId && t.TemplateId == req.TemplateId, ct))
            return Conflict(new { error = $"TemplateId '{req.TemplateId}' 在 Workspace '{req.WorkspaceId}' 中已存在" });

        // ADR-034：校验或补全 AvatarId
        var resolvedAvatarId = await ResolveAvatarIdAsync(req.AvatarId);
        if (resolvedAvatarId is null)
            return BadRequest(new { error = "没有可用的系统头像，请先配置头像资源" });

        var entity = BuildEntity(req);
        entity.AvatarId = resolvedAvatarId;
        db.WorkspaceAgentTemplates.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, await MapToDtoAsync(entity));
    }

    // ── 更新 ──────────────────────────────────────────────────────
    [HttpPut("{id:int}")]
    public async Task<ActionResult<WorkspaceAgentTemplateDto>> Update(
        int id, [FromBody] UpsertWorkspaceAgentTemplateRequest req, CancellationToken ct)
    {
        var entity = await db.WorkspaceAgentTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
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
        entity.MemoryLlmEndpoint = req.MemoryLlmEndpoint;
        entity.MemoryLlmApiKey = req.MemoryLlmApiKey;
        entity.MemoryLlmModelId = req.MemoryLlmModelId;
        entity.MemorySearchMode = req.MemorySearchMode ?? "deep";
        entity.ReasoningEffort = req.ReasoningEffort;
        entity.MaxContextTokens = req.MaxContextTokens;
        entity.MaxReplyTokens = req.MaxReplyTokens;
        entity.ContainerImage = req.ContainerImage;
        entity.BaseGlobalTemplateId = req.BaseGlobalTemplateId;
        entity.SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []);
        entity.IsEnabled = req.IsEnabled;
        entity.SortOrder = req.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        return Ok(MapToDto(entity, avatarMap));
    }

    // ── 删除 ──────────────────────────────────────────────────────
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await db.WorkspaceAgentTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return NotFound();

        db.WorkspaceAgentTemplates.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static WorkspaceAgentTemplateEntity BuildEntity(UpsertWorkspaceAgentTemplateRequest req) => new()
    {
        WorkspaceId = req.WorkspaceId,
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
        BaseGlobalTemplateId = req.BaseGlobalTemplateId,
        SelectedCapabilityIdsJson = JsonSerializer.Serialize(req.SelectedCapabilityIds ?? []),
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
    };

    private WorkspaceAgentTemplateDto MapToDto(WorkspaceAgentTemplateEntity t, Dictionary<string, AgentAvatarEntity>? avatarMap = null)
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
            t.Id, t.WorkspaceId, t.TemplateId, t.Name, t.Description, t.Role,
            t.SystemPrompt, t.UserPromptTemplate,
            t.PreferredProviderId, t.PreferredModelId,
            t.MaxContextTokens, t.MaxReplyTokens,
            t.ContainerImage,
            t.BaseGlobalTemplateId, ParseStringList(t.SelectedCapabilityIdsJson), t.IsEnabled, t.SortOrder,
            t.CreatedAt, t.UpdatedAt,
            t.PersonaPrompt, t.ToolsDescription, t.BootstrapTemplate, t.AvatarEmoji,
            t.AvatarId,
            avatar?.UrlPath,
            avatar?.Name,
            t.MemoryLlmEndpoint, t.MemoryLlmApiKey, t.MemoryLlmModelId, t.MemorySearchMode,
            t.ReasoningEffort);
    }

    private async Task<string?> ResolveAvatarIdAsync(string? avatarId)
    {
        if (!string.IsNullOrWhiteSpace(avatarId))
        {
            var avatar = await avatarCatalog.GetRequiredEnabledAsync(avatarId);
            if (avatar is not null) return avatarId;
            return null;
        }

        var defaultAvatar = await avatarCatalog.GetDefaultAsync();
        return defaultAvatar?.AvatarId;
    }

    private async Task<WorkspaceAgentTemplateDto> MapToDtoAsync(WorkspaceAgentTemplateEntity t)
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
