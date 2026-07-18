using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatform.Controllers.Api;

[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/agents/{agentId}/message-queue")]
public sealed class MessageQueueController(MessageQueueProjectionService projectionService) : ControllerBase
{
    /// <summary>
    /// Returns the backend-owned delivery queue for an agent.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<MessageQueueSnapshot>> GetAgentQueue(
        string workspaceId,
        string agentId,
        [FromQuery] string? roomId = null,
        [FromQuery] int limit = 50,
        [FromQuery] bool includeTerminal = false,
        [FromQuery] bool includeSystem = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { message = "workspaceId is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { message = "agentId is required." });

        var snapshot = await projectionService.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            RoomId = string.IsNullOrWhiteSpace(roomId) ? null : roomId,
            Limit = limit,
            IncludeTerminal = includeTerminal,
            IncludeSystem = includeSystem,
        }, ct);

        return Ok(snapshot);
    }
}
