using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 工作空间内的 Agent 实例管理 API（文件式配置，存储在 data/agents/ 和 data/workspaces/）。
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/agents")]
public class WorkspaceAgentApiController(
    WorkspaceAgentFileService fileService,
    ILogger<WorkspaceAgentApiController> logger) : ControllerBase
{
    // GET /api/workspaces/{workspaceId}/agents
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceAgentDto>>> List(string workspaceId, CancellationToken ct)
    {
        var agents = await fileService.ListAgentsAsync(workspaceId, ct);
        return Ok(agents);
    }

    // GET /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpGet("{agentId}")]
    public async Task<ActionResult<WorkspaceAgentDto>> Get(string workspaceId, string agentId, CancellationToken ct)
    {
        var agent = await fileService.GetAgentAsync(workspaceId, agentId, ct);
        if (agent is null) return NotFound();
        return Ok(agent);
    }

    // POST /api/workspaces/{workspaceId}/agents
    [HttpPost]
    public async Task<ActionResult<WorkspaceAgentDto>> Create(
        string workspaceId, [FromBody] CreateWorkspaceAgentRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.CreateAgentAsync(workspaceId, req, ct);
            return CreatedAtAction(nameof(Get),
                new { workspaceId, agentId = result.AgentId }, result);
        }
        catch (WorkspaceAuditAgentConflictException ex)
        {
            logger.LogWarning(ex, "[WorkspaceAgentApi] Create conflict workspace={WorkspaceId}", workspaceId);
            return Conflict(new
            {
                message = "Agent ID 已存在，请更换后重试。",
                workspaceId = ex.WorkspaceId,
                existingAgentId = ex.ExistingAgentId,
            });
        }
    }

    // PUT /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpPut("{agentId}")]
    public async Task<ActionResult<WorkspaceAgentDto>> Update(
        string workspaceId, string agentId,
        [FromBody] UpdateWorkspaceAgentRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.UpdateAgentAsync(workspaceId, agentId, req, ct);
            return Ok(result);
        }
        catch (WorkspaceAuditAgentConflictException ex)
        {
            logger.LogWarning(ex, "[WorkspaceAgentApi] Update conflict workspace={WorkspaceId} agent={AgentId}", workspaceId, agentId);
            return Conflict(new
            {
                message = "Agent ID 已存在，请更换后重试。",
                workspaceId = ex.WorkspaceId,
                existingAgentId = ex.ExistingAgentId,
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // POST /api/workspaces/{workspaceId}/agents/{agentId}/freeze
    [HttpPost("{agentId}/freeze")]
    public async Task<IActionResult> Freeze(string workspaceId, string agentId, CancellationToken ct)
    {
        var agent = await fileService.SetFrozenAsync(workspaceId, agentId, frozen: true, ct);
        return Ok(agent);
    }

    // POST /api/workspaces/{workspaceId}/agents/{agentId}/unfreeze
    [HttpPost("{agentId}/unfreeze")]
    public async Task<IActionResult> Unfreeze(string workspaceId, string agentId, CancellationToken ct)
    {
        var agent = await fileService.SetFrozenAsync(workspaceId, agentId, frozen: false, ct);
        return Ok(agent);
    }

    // DELETE /api/workspaces/{workspaceId}/agents/{agentId}
    [HttpDelete("{agentId}")]
    public async Task<IActionResult> Delete(string workspaceId, string agentId, CancellationToken ct)
    {
        try
        {
            await fileService.DeleteAgentAsync(workspaceId, agentId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
