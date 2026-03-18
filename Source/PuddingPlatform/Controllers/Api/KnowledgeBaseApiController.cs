using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的知识库管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/knowledge-bases")]
public class KnowledgeBaseApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/knowledge-bases
    [HttpGet]
    public async Task<ActionResult<List<KnowledgeBaseDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var list = await db.KnowledgeBases
            .AsNoTracking()
            .Where(k => k.WorkspaceEntityId == ws.Id)
            .OrderBy(k => k.Id)
            .Select(k => ToDto(k))
            .ToListAsync(ct);

        return Ok(list);
    }

    // GET /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpGet("{kbId}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Get(string workspaceId, string kbId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var kb = await db.KnowledgeBases
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.WorkspaceEntityId == ws.Id && k.KbId == kbId, ct);

        return kb is null ? NotFound() : Ok(ToDto(kb));
    }

    // POST /api/workspaces/{workspaceId}/knowledge-bases
    [HttpPost]
    public async Task<ActionResult<KnowledgeBaseDto>> Create(
        string workspaceId, [FromBody] UpsertKnowledgeBaseRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = new KnowledgeBaseEntity
        {
            KbId              = Guid.NewGuid().ToString(),
            WorkspaceEntityId = ws.Id,
            Name              = req.Name,
            Description       = req.Description,
            KbType            = req.KbType,
            DocumentCount     = 0,
            IsEnabled         = req.IsEnabled,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow,
        };
        db.KnowledgeBases.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, kbId = entity.KbId }, ToDto(entity));
    }

    // PUT /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpPut("{kbId}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Update(
        string workspaceId, string kbId,
        [FromBody] UpsertKnowledgeBaseRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var kb = await db.KnowledgeBases
            .FirstOrDefaultAsync(k => k.WorkspaceEntityId == ws.Id && k.KbId == kbId, ct);
        if (kb is null) return NotFound();

        kb.Name        = req.Name;
        kb.Description = req.Description;
        kb.KbType      = req.KbType;
        kb.IsEnabled   = req.IsEnabled;
        kb.UpdatedAt   = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(kb));
    }

    // DELETE /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpDelete("{kbId}")]
    public async Task<IActionResult> Delete(string workspaceId, string kbId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var kb = await db.KnowledgeBases
            .FirstOrDefaultAsync(k => k.WorkspaceEntityId == ws.Id && k.KbId == kbId, ct);
        if (kb is null) return NotFound();

        db.KnowledgeBases.Remove(kb);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static KnowledgeBaseDto ToDto(KnowledgeBaseEntity e) => new(
        e.KbId, e.Name, e.Description, e.KbType,
        e.DocumentCount, e.IsEnabled, e.CreatedAt, e.UpdatedAt);
}
