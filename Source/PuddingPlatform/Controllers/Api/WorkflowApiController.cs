using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作空间内的工作流管理 API。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/workflows")]
public class WorkflowApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/workflows
    [HttpGet]
    public async Task<ActionResult<List<WorkflowDto>>> List(string workspaceId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var list = await db.Workflows
            .AsNoTracking()
            .Where(w => w.WorkspaceEntityId == ws.Id)
            .OrderBy(w => w.Id)
            .Select(w => ToDto(w))
            .ToListAsync(ct);

        return Ok(list);
    }

    // GET /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpGet("{workflowId}")]
    public async Task<ActionResult<WorkflowDto>> Get(string workspaceId, string workflowId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var workflow = await db.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceEntityId == ws.Id && w.WorkflowId == workflowId, ct);

        return workflow is null ? NotFound() : Ok(ToDto(workflow));
    }

    // POST /api/workspaces/{workspaceId}/workflows
    [HttpPost]
    public async Task<ActionResult<WorkflowDto>> Create(
        string workspaceId, [FromBody] UpsertWorkflowRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = new WorkflowEntity
        {
            WorkflowId        = Guid.NewGuid().ToString(),
            WorkspaceEntityId = ws.Id,
            Name              = req.Name,
            Description       = req.Description,
            DefinitionJson    = req.DefinitionJson,
            Status            = req.Status,
            IsEnabled         = req.IsEnabled,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow,
        };
        db.Workflows.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { workspaceId, workflowId = entity.WorkflowId }, ToDto(entity));
    }

    // PUT /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpPut("{workflowId}")]
    public async Task<ActionResult<WorkflowDto>> Update(
        string workspaceId, string workflowId,
        [FromBody] UpsertWorkflowRequest req, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var workflow = await db.Workflows
            .FirstOrDefaultAsync(w => w.WorkspaceEntityId == ws.Id && w.WorkflowId == workflowId, ct);
        if (workflow is null) return NotFound();

        workflow.Name           = req.Name;
        workflow.Description    = req.Description;
        workflow.DefinitionJson = req.DefinitionJson;
        workflow.Status         = req.Status;
        workflow.IsEnabled      = req.IsEnabled;
        workflow.UpdatedAt      = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(workflow));
    }

    // DELETE /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpDelete("{workflowId}")]
    public async Task<IActionResult> Delete(string workspaceId, string workflowId, CancellationToken ct)
    {
        var ws = await GetWorkspaceAsync(workspaceId, ct);
        if (ws is null) return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var workflow = await db.Workflows
            .FirstOrDefaultAsync(w => w.WorkspaceEntityId == ws.Id && w.WorkflowId == workflowId, ct);
        if (workflow is null) return NotFound();

        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<WorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkflowDto ToDto(WorkflowEntity e) => new(
        e.WorkflowId, e.Name, e.Description, e.DefinitionJson,
        e.Status, e.IsEnabled, e.CreatedAt, e.UpdatedAt);
}
