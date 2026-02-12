using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Agent-first chat client API for /admin/chat.</summary>
[ApiController]
[Route("api/workspaces/{workspaceId}/agents")]
public sealed class AgentChatApiController(IAgentRunProjectionService projection) : ControllerBase
{
    /// <summary>Returns contact-list status projections for all Agent main sessions in a workspace.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<IReadOnlyList<AgentStatusProjection>>> GetStatuses(
        string workspaceId,
        CancellationToken ct)
    {
        var ownerUserId = ResolveOwnerUserId();
        var statuses = await projection.GetWorkspaceAgentStatusesAsync(workspaceId, ownerUserId, ct);
        return Ok(statuses);
    }

    /// <summary>Returns a renderable conversation projection for one Agent main session.</summary>
    [HttpGet("{agentId}/conversation")]
    public async Task<ActionResult<AgentConversationView>> GetConversation(
        string workspaceId,
        string agentId,
        [FromQuery] int? knownCursor,
        [FromServices] IAgentConversationProjectionService conversation,
        CancellationToken ct)
    {
        var ownerUserId = ResolveOwnerUserId();
        // P0-perf: 如果前端已知 cursor 且后端未变化，直接返回 304 避免构建 3MB projection
        if (knownCursor.HasValue && knownCursor.Value > 0)
        {
            var currentCursor = await conversation.GetConversationCursorAsync(workspaceId, ownerUserId, agentId, ct);
            if (currentCursor == knownCursor.Value)
            {
                return StatusCode(304);
            }
        }

        var view = await conversation.GetConversationAsync(workspaceId, ownerUserId, agentId, ct);
        return Ok(view);
    }

    private string ResolveOwnerUserId() =>
        User.Identity?.Name ?? "single-user";
}
