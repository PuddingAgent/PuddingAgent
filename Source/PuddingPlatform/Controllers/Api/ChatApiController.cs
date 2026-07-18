using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Chat API 旧路由兼容层 — POST /api/workspaces/{ws}/chat/message → ISubmitTurnHandler。
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/chat")]
public class ChatApiController(
    ISubmitTurnHandler handler) : ControllerBase
{
    [HttpPost("message")]
    public async Task<IActionResult> SendMessage(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        // Use req.SessionId as conversationId for consistency with SSE listener.
        // "main" or specific sessionId → same conversationId in SSE and POST.
        var conversationId = !string.IsNullOrWhiteSpace(req.SessionId)
            ? req.SessionId.Trim()
            : Guid.NewGuid().ToString("N");

        var command = new SubmitTurnCommand(
            ConversationId: conversationId,
            WorkspaceId: workspaceId,
            UserId: User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.Identity?.Name ?? "admin",
            ClientRequestId: req.ClientRequestId ?? Guid.NewGuid().ToString("N"),
            ClientMessageId: Guid.NewGuid().ToString("N"),
            Recipients: new RecipientRequest
            {
                Type = "agent",
                AgentIds = req.TargetAgentIds ?? [req.AgentId ?? "default"],
            },
            Content: [new ContentPart { Type = "text", Text = req.MessageText }]);

        var result = await handler.HandleAsync(command, ct);

        return StatusCode(202, new
        {
            success = true,
            status = "accepted",
            conversationId = result.ConversationId,
            commandId = result.CommandIds.FirstOrDefault(),
            messageId = result.MessageId,
            turnId = result.TurnIds.FirstOrDefault(),
            eventCursor = result.AcceptedSequence,
        });
    }
}
