using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>工作流 HTTP 出口。校验与数据访问在 WorkspaceResourceService（Web 与原生客户端共用）。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/workflows")]
public class WorkflowApiController(WorkspaceResourceService resources) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/workflows
    [HttpGet]
    public async Task<ActionResult<List<WorkflowDto>>> List(string workspaceId, CancellationToken ct)
    {
        var result = await resources.ListWorkflowsAsync(workspaceId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // GET /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpGet("{workflowId}")]
    public async Task<ActionResult<WorkflowDto>> Get(string workspaceId, string workflowId, CancellationToken ct)
    {
        var result = await resources.ListWorkflowsAsync(workspaceId, ct);
        if (!result.IsOk) return Problem(result);
        var workflow = result.Value!.FirstOrDefault(item => item.WorkflowId == workflowId);
        return workflow is null ? NotFound() : Ok(workflow);
    }

    // POST /api/workspaces/{workspaceId}/workflows
    [HttpPost]
    public async Task<ActionResult<WorkflowDto>> Create(
        string workspaceId, [FromBody] UpsertWorkflowRequest req, CancellationToken ct)
    {
        var result = await resources.CreateWorkflowAsync(workspaceId,
            new WorkspaceWorkflowDraft(req.Name, req.Description, req.DefinitionJson ?? "", req.Status, req.IsEnabled), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { workspaceId, workflowId = result.Value!.WorkflowId }, result.Value)
            : Problem(result);
    }

    // PUT /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpPut("{workflowId}")]
    public async Task<ActionResult<WorkflowDto>> Update(
        string workspaceId, string workflowId, [FromBody] UpsertWorkflowRequest req, CancellationToken ct)
    {
        var result = await resources.UpdateWorkflowAsync(workspaceId, workflowId,
            new WorkspaceWorkflowDraft(req.Name, req.Description, req.DefinitionJson ?? "", req.Status, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // DELETE /api/workspaces/{workspaceId}/workflows/{workflowId}
    [HttpDelete("{workflowId}")]
    public async Task<IActionResult> Delete(string workspaceId, string workflowId, CancellationToken ct)
    {
        var result = await resources.DeleteWorkflowAsync(workspaceId, workflowId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    private ActionResult Problem<T>(SkillHubResult<T> result) where T : class =>
        result.Status switch
        {
            SkillHubStatus.BadRequest => BadRequest(new { message = result.Error }),
            SkillHubStatus.Conflict => Conflict(new { message = result.Error }),
            _ => NotFound(new { message = result.Error })
        };
}
