using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的 Agent 实例管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/agents")]
public class WorkspaceAgentApiController(
    PlatformDbContext db,
    AgentAvatarCatalog avatarCatalog) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/agents
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceAgentDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var agents = await db.WorkspaceAgents
            .AsNoTracking()
            .Where(a => a.WorkspaceEntityId == ws.Id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        // 批量收集 SourceTemplateId，避免 N+1
        var templateIds = agents
            .Where(a => !string.IsNullOrWhiteSpace(a.SourceTemplateId))
            .Select(a => a.SourceTemplateId!)
            .Distinct()
            .ToList();

        // 查 Workspace 模板和全局模板的 AvatarId
        var workspaceTemplates = templateIds.Count > 0
            ? await db.WorkspaceAgentTemplates
                .AsNoTracking()
                .Where(t => templateIds.Contains(t.TemplateId))
                .Select(t => new { t.TemplateId, t.AvatarId })
                .ToListAsync(ct)
            : [];

        var globalTemplates = templateIds.Count > 0
            ? await db.GlobalAgentTemplates
                .AsNoTracking()
                .Where(t => templateIds.Contains(t.TemplateId))
                .Select(t => new { t.TemplateId, t.AvatarId })
                .ToListAsync(ct)
            : [];

        var templateAvatarMap = new Dictionary<string, string?>();
        foreach (var wt in workspaceTemplates)
            templateAvatarMap[wt.TemplateId] = wt.AvatarId;
        foreach (var gt in globalTemplates)
            if (!templateAvatarMap.ContainsKey(gt.TemplateId))
                templateAvatarMap[gt.TemplateId] = gt.AvatarId;

        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        var defaultAvatar = avatarMap.Values.MinBy(a => (a.SortOrder, a.Id));

        var dtos = agents.Select(agent =>
        {
            var (resolvedAvatarId, resolvedUrl) = ResolveAgentAvatar(
                agent, templateAvatarMap, avatarMap, defaultAvatar);
            return ToDto(agent, resolvedAvatarId, resolvedUrl);
        }).ToList();

        return Ok(dtos);
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
        if (agent is null) return NotFound();

        // 解析模板头像
        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        var defaultAvatar = avatarMap.Values.MinBy(a => (a.SortOrder, a.Id));
        string? templateAvatarId = null;
        if (!string.IsNullOrWhiteSpace(agent.SourceTemplateId))
        {
            var wsTemplate = await db.WorkspaceAgentTemplates
                .AsNoTracking().FirstOrDefaultAsync(t => t.TemplateId == agent.SourceTemplateId, ct);
            templateAvatarId = wsTemplate?.AvatarId;
            if (string.IsNullOrWhiteSpace(templateAvatarId))
            {
                var globalTemplate = await db.GlobalAgentTemplates
                    .AsNoTracking().FirstOrDefaultAsync(t => t.TemplateId == agent.SourceTemplateId, ct);
                templateAvatarId = globalTemplate?.AvatarId;
            }
        }
        var templateAvatarMap = !string.IsNullOrWhiteSpace(agent.SourceTemplateId)
            ? new Dictionary<string, string?> { [agent.SourceTemplateId] = templateAvatarId }
            : [];
        var (resolvedAvatarId, resolvedUrl) = ResolveAgentAvatar(
            agent, templateAvatarMap, avatarMap, defaultAvatar);

        return Ok(ToDto(agent, resolvedAvatarId, resolvedUrl));
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
            AvatarId             = req.AvatarId,
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

        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        var defaultAvatar = avatarMap.Values.MinBy(a => (a.SortOrder, a.Id));
        var (resolvedAvatarId, resolvedUrl) = ResolveAgentAvatar(
            entity, [], avatarMap, defaultAvatar);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, agentId = entity.AgentId },
            ToDto(entity, resolvedAvatarId, resolvedUrl));
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
        agent.AvatarId             = req.AvatarId;
        agent.AvatarUrl            = req.AvatarUrl;
        agent.SourceTemplateId     = req.SourceTemplateId;
        agent.SystemPromptOverride = req.SystemPromptOverride;
        agent.PreferredProviderId  = req.PreferredProviderId;
        agent.PreferredModelId     = req.PreferredModelId;
        agent.IsEnabled            = req.IsEnabled;
        agent.UpdatedAt            = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var avatarMap = await avatarCatalog.GetEnabledMapAsync();
        var defaultAvatar = avatarMap.Values.MinBy(a => (a.SortOrder, a.Id));
        string? templateAvatarId = null;
        if (!string.IsNullOrWhiteSpace(agent.SourceTemplateId))
        {
            var wsTemplate = await db.WorkspaceAgentTemplates
                .AsNoTracking().FirstOrDefaultAsync(t => t.TemplateId == agent.SourceTemplateId, ct);
            templateAvatarId = wsTemplate?.AvatarId;
            if (string.IsNullOrWhiteSpace(templateAvatarId))
            {
                var globalTemplate = await db.GlobalAgentTemplates
                    .AsNoTracking().FirstOrDefaultAsync(t => t.TemplateId == agent.SourceTemplateId, ct);
                templateAvatarId = globalTemplate?.AvatarId;
            }
        }
        var templateAvatarMap = !string.IsNullOrWhiteSpace(agent.SourceTemplateId)
            ? new Dictionary<string, string?> { [agent.SourceTemplateId] = templateAvatarId }
            : [];
        var (resolvedAvatarId, resolvedUrl) = ResolveAgentAvatar(
            agent, templateAvatarMap, avatarMap, defaultAvatar);

        return Ok(ToDto(agent, resolvedAvatarId, resolvedUrl));
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

    /// <summary>
    /// 解析 Agent 头像优先级：
    /// 1. Agent 自身 AvatarId
    /// 2. SourceTemplateId -> template AvatarId
    /// 3. Agent 自身 AvatarUrl (legacy)
    /// 4. 系统默认头像
    /// 5. null
    /// </summary>
    private static (string? AvatarId, string? AvatarUrl) ResolveAgentAvatar(
        WorkspaceAgentEntity agent,
        Dictionary<string, string?> templateAvatarMap,
        Dictionary<string, AgentAvatarEntity> avatarMap,
        AgentAvatarEntity? defaultAvatar)
    {
        // 1. 自身 AvatarId
        if (!string.IsNullOrWhiteSpace(agent.AvatarId) && avatarMap.TryGetValue(agent.AvatarId, out var selfAvatar))
            return (selfAvatar.AvatarId, selfAvatar.UrlPath);

        // 2. SourceTemplateId -> template AvatarId
        if (!string.IsNullOrWhiteSpace(agent.SourceTemplateId)
            && templateAvatarMap.TryGetValue(agent.SourceTemplateId, out var templAvatarId)
            && !string.IsNullOrWhiteSpace(templAvatarId)
            && avatarMap.TryGetValue(templAvatarId, out var templAvatar))
            return (templAvatar.AvatarId, templAvatar.UrlPath);

        // 3. Legacy AvatarUrl
        if (!string.IsNullOrWhiteSpace(agent.AvatarUrl))
            return (null, agent.AvatarUrl);

        // 4. 系统默认头像
        if (defaultAvatar is not null)
            return (defaultAvatar.AvatarId, defaultAvatar.UrlPath);

        // 5. 无头像
        return (null, null);
    }

    private static WorkspaceAgentDto ToDto(
        WorkspaceAgentEntity e, string? resolvedAvatarId, string? resolvedAvatarUrl) => new(
        e.AgentId, e.Name, e.Description,
        e.DisplayName, resolvedAvatarId, resolvedAvatarUrl,
        e.SourceTemplateId,
        e.SystemPromptOverride, e.PreferredProviderId, e.PreferredModelId,
        e.IsEnabled, e.IsFrozen, e.CreatedAt, e.UpdatedAt);
}
