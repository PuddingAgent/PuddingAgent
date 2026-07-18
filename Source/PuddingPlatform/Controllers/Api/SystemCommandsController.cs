using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Executes system-owned chat commands. These commands never enter the Agent turn pipeline.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/conversations")]
public sealed class SystemCommandsController : ControllerBase
{
    [HttpPost("{conversationId}/system-commands")]
    public async Task<ActionResult<SystemCommandResult>> Execute(
        [FromRoute] string conversationId,
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        [FromBody] ExecuteSystemCommandHttpRequest request,
        [FromServices] ISystemCommandHandler handler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId) ||
            string.IsNullOrWhiteSpace(workspaceId) ||
            string.IsNullOrWhiteSpace(request.AgentId) ||
            string.IsNullOrWhiteSpace(request.ClientRequestId) ||
            string.IsNullOrWhiteSpace(request.ClientMessageId) ||
            string.IsNullOrWhiteSpace(request.ResponseMessageId) ||
            string.IsNullOrWhiteSpace(request.CommandText))
        {
            return ValidationProblem(
                title: "The system command request is invalid.",
                detail: "Conversation, workspace, agent, idempotency IDs, and command text are required.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new SystemCommandRequest(
                    conversationId,
                    workspaceId,
                    request.AgentId,
                    ResolveUserId(),
                    request.ClientRequestId,
                    request.ClientMessageId,
                    request.ResponseMessageId,
                    request.CommandText),
                ct);
            return Ok(result);
        }
        catch (NotSupportedException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Unsupported system command",
                detail: ex.Message);
        }
    }

    private string ResolveUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("User identity required.");
}

public sealed record ExecuteSystemCommandHttpRequest(
    string AgentId,
    string ClientRequestId,
    string ClientMessageId,
    string ResponseMessageId,
    string CommandText);
