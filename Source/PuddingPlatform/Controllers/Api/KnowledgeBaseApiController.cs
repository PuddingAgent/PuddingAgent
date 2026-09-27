using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>知识库 HTTP 出口。校验与数据访问在 WorkspaceResourceService（Web 与原生客户端共用）。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/knowledge-bases")]
public class KnowledgeBaseApiController(WorkspaceResourceService resources) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/knowledge-bases
    [HttpGet]
    public async Task<ActionResult<List<KnowledgeBaseDto>>> List(string workspaceId, CancellationToken ct)
    {
        var result = await resources.ListKnowledgeBasesAsync(workspaceId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // GET /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpGet("{kbId}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Get(string workspaceId, string kbId, CancellationToken ct)
    {
        var result = await resources.ListKnowledgeBasesAsync(workspaceId, ct);
        if (!result.IsOk) return Problem(result);
        var kb = result.Value!.FirstOrDefault(item => item.KbId == kbId);
        return kb is null ? NotFound() : Ok(kb);
    }

    // POST /api/workspaces/{workspaceId}/knowledge-bases
    [HttpPost]
    public async Task<ActionResult<KnowledgeBaseDto>> Create(
        string workspaceId, [FromBody] UpsertKnowledgeBaseRequest req, CancellationToken ct)
    {
        var result = await resources.CreateKnowledgeBaseAsync(workspaceId,
            new KnowledgeBaseDraft(req.Name, req.Description, req.KbType, req.IsEnabled), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { workspaceId, kbId = result.Value!.KbId }, result.Value)
            : Problem(result);
    }

    // PUT /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpPut("{kbId}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Update(
        string workspaceId, string kbId, [FromBody] UpsertKnowledgeBaseRequest req, CancellationToken ct)
    {
        var result = await resources.UpdateKnowledgeBaseAsync(workspaceId, kbId,
            new KnowledgeBaseDraft(req.Name, req.Description, req.KbType, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // DELETE /api/workspaces/{workspaceId}/knowledge-bases/{kbId}
    [HttpDelete("{kbId}")]
    public async Task<IActionResult> Delete(string workspaceId, string kbId, CancellationToken ct)
    {
        var result = await resources.DeleteKnowledgeBaseAsync(workspaceId, kbId, ct);
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
