using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Runtime;
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
    IAgentAccessLevelService accessLevels,
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

    // GET /api/workspaces/{workspaceId}/agents/{agentId}/access-level
    // 读：前端下拉在切换到不同 Agent 时靠它同步状态（用户 2026-09-19 需求 4）。
    [HttpGet("{agentId}/access-level")]
    public ActionResult<AgentAccessLevelDto> GetAccessLevel(string workspaceId, string agentId)
        => Ok(ToAccessLevelDto(agentId, accessLevels.Get(agentId)));

    // PUT /api/workspaces/{workspaceId}/agents/{agentId}/access-level
    // 写：设为 auto（撤销）或 full（durationSeconds 为空=持久；非空=临时授权）。
    // 授予「完全访问」是高权限动作，与 /yolo 命令同级，故限 admin。
    [HttpPut("{agentId}/access-level")]
    [Authorize(Roles = "admin")]
    public ActionResult<AgentAccessLevelDto> SetAccessLevel(
        string workspaceId,
        string agentId,
        [FromBody] SetAgentAccessLevelRequest request)
    {
        if (!Enum.TryParse<AgentAccessLevel>(request.Level, ignoreCase: true, out var level))
            return BadRequest(new { message = "level 必须是 auto 或 full。" });

        if (request.DurationSeconds is <= 0)
            return BadRequest(new { message = "durationSeconds 必须为正整数，或留空表示持久授权。" });

        try
        {
            var ttl = request.DurationSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
            var state = accessLevels.Set(agentId, level, ttl, User?.Identity?.Name ?? "admin");
            return Ok(ToAccessLevelDto(agentId, state));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[WorkspaceAgentApi] Access level store unavailable agent={AgentId}", agentId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Agent 访问级别存储不可用（宿主未配置数据目录）。",
            });
        }
    }

    private static AgentAccessLevelDto ToAccessLevelDto(string agentId, AgentAccessLevelState state)
    {
        var now = DateTimeOffset.UtcNow;
        var active = state.IsFullAt(now);
        return new AgentAccessLevelDto(
            agentId,
            active ? "full" : "auto",
            active,
            active && state.ExpiresAtUtc is { } expires ? expires.ToUniversalTime().ToString("O") : null,
            active && state.IsTemporary);
    }
}

/// <summary>Agent 访问级别读结果。过期后 Level 会如实回落为 auto（不返回陈旧值）。</summary>
public sealed record AgentAccessLevelDto(
    string AgentId,
    string Level,
    bool FullAccessActive,
    string? ExpiresAtUtc,
    bool Temporary);

/// <summary>Agent 访问级别写请求。DurationSeconds 留空 = 持久授权；非空 = 临时授权。</summary>
public sealed record SetAgentAccessLevelRequest(string Level, int? DurationSeconds = null);
