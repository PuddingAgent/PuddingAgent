using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Conversation Turns API — ADR-059。
/// 依赖 4 个独立 Handler，每个 ~15 行。
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/conversations")]
public class ConversationTurnsController : ControllerBase
{
    /// <summary>提交 Turn — POST /{conversationId}/turns</summary>
    [HttpPost("{conversationId}/turns")]
    public async Task<IActionResult> SubmitTurn(
        [FromRoute] string conversationId,
        [FromBody] SubmitTurnHttpRequest request,
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        [FromServices] ISubmitTurnHandler handler,
        CancellationToken ct)
    {
        var command = new SubmitTurnCommand(
            ConversationId: conversationId,
            WorkspaceId: workspaceId,
            UserId: ResolveUserId(),
            ClientRequestId: request.ClientRequestId,
            ClientMessageId: request.ClientMessageId,
            Recipients: request.Recipients,
            Content: request.Content);

        var result = await handler.HandleAsync(command, ct);
        return Accepted(result);
    }

    /// <summary>取消 Turn — POST /{conversationId}/turns/{turnId}/cancel</summary>
    [HttpPost("{conversationId}/turns/{turnId}/cancel")]
    public async Task<IActionResult> CancelTurn(
        [FromRoute] string conversationId,
        [FromRoute] string turnId,
        [FromServices] IRequestTurnCancellationHandler handler,
        CancellationToken ct)
    {
        var command = new RequestTurnCancellationCommand(
            ConversationId: conversationId,
            TurnId: turnId,
            UserId: ResolveUserId());
        var result = await handler.HandleAsync(command, ct);
        return Ok(result);
    }

    /// <summary>Steering — POST /{conversationId}/turns/{turnId}/steering</summary>
    [HttpPost("{conversationId}/turns/{turnId}/steering")]
    public async Task<IActionResult> CreateSteering(
        [FromRoute] string conversationId,
        [FromRoute] string turnId,
        [FromBody] SteeringHttpRequest request,
        [FromServices] ICreateSteeringHandler handler,
        CancellationToken ct)
    {
        var command = new CreateSteeringCommand(
            ConversationId: conversationId,
            TurnId: turnId,
            Text: request.Text,
            Priority: request.Priority,
            UserId: ResolveUserId());
        await handler.HandleAsync(command, ct);
        return Accepted();
    }

    private string ResolveUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("User identity required.");
}

/// <summary>SubmitTurn HTTP DTO — Controller 层专用。</summary>
public sealed record SubmitTurnHttpRequest
{
    public required string ClientRequestId { get; init; }
    public required string ClientMessageId { get; init; }
    public required RecipientRequest Recipients { get; init; }
    public required IReadOnlyList<ContentPart> Content { get; init; }
}

/// <summary>Steering HTTP DTO — Controller 层专用。</summary>
public sealed record SteeringHttpRequest
{
    public required string Text { get; init; }
    public int Priority { get; init; } = 100;
}
