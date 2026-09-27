using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 工作区技能 HTTP 出口。校验（含 MCP configJson 的解析与规范化）与数据访问在
/// WorkspaceResourceService；运行状态仍直接读 MCP 连接管理器（不是工作区 CRUD）。
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/skills")]
public class WorkspaceSkillApiController(
    WorkspaceResourceService resources,
    IMcpConnectionManager? mcpConnectionManager = null) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/skills
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceSkillDto>>> List(string workspaceId, CancellationToken ct)
    {
        var result = await resources.ListSkillsAsync(workspaceId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // GET /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpGet("{skillId}")]
    public async Task<ActionResult<WorkspaceSkillDto>> Get(string workspaceId, string skillId, CancellationToken ct)
    {
        var result = await resources.ListSkillsAsync(workspaceId, ct);
        if (!result.IsOk) return Problem(result);
        var skill = result.Value!.FirstOrDefault(item => item.SkillId == skillId);
        return skill is null ? NotFound() : Ok(skill);
    }

    // POST /api/workspaces/{workspaceId}/skills
    [HttpPost]
    public async Task<ActionResult<WorkspaceSkillDto>> Create(
        string workspaceId, [FromBody] UpsertWorkspaceSkillRequest req, CancellationToken ct)
    {
        var result = await resources.CreateSkillAsync(workspaceId,
            new WorkspaceSkillDraft(req.Name, req.Description, req.SkillType, req.ConfigJson, req.IsEnabled), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { workspaceId, skillId = result.Value!.SkillId }, result.Value)
            : Problem(result);
    }

    // PUT /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpPut("{skillId}")]
    public async Task<ActionResult<WorkspaceSkillDto>> Update(
        string workspaceId, string skillId, [FromBody] UpsertWorkspaceSkillRequest req, CancellationToken ct)
    {
        var result = await resources.UpdateSkillAsync(workspaceId, skillId,
            new WorkspaceSkillDraft(req.Name, req.Description, req.SkillType, req.ConfigJson, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // DELETE /api/workspaces/{workspaceId}/skills/{skillId}
    [HttpDelete("{skillId}")]
    public async Task<IActionResult> Delete(string workspaceId, string skillId, CancellationToken ct)
    {
        var result = await resources.DeleteSkillAsync(workspaceId, skillId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    [HttpGet("{skillId}/runtime-status")]
    public async Task<ActionResult<McpServerRuntimeStatus>> GetRuntimeStatus(
        string workspaceId, string skillId, CancellationToken ct)
    {
        var listed = await resources.ListSkillsAsync(workspaceId, ct);
        if (!listed.IsOk) return Problem(listed);
        if (!listed.Value!.Any(skill => skill.SkillId == skillId) || mcpConnectionManager is null) return NotFound();

        var status = mcpConnectionManager.ListStatuses(workspaceId)
            .FirstOrDefault(item => item.SkillId == skillId);
        return status is null ? NotFound() : Ok(status);
    }

    private ActionResult Problem<T>(SkillHubResult<T> result) where T : class =>
        result.Status switch
        {
            SkillHubStatus.BadRequest => BadRequest(new { message = result.Error }),
            SkillHubStatus.Conflict => Conflict(new { message = result.Error }),
            _ => NotFound(new { message = result.Error })
        };
}
