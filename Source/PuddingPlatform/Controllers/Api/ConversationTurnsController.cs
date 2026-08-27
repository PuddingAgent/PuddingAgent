using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Core;
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
        var validationResult = ValidateSubmitTurn(conversationId, workspaceId, request);
        if (validationResult is not null)
            return validationResult;

        var command = new SubmitTurnCommand(
            ConversationId: conversationId,
            WorkspaceId: workspaceId,
            UserId: ResolveUserId(),
            ClientRequestId: request.ClientRequestId,
            ClientMessageId: request.ClientMessageId,
            Recipients: request.Recipients,
            Content: request.Content,
            Metadata: request.Metadata);

        AcceptanceResult result;
        try
        {
            result = await handler.HandleAsync(command, ct);
        }
        catch (PuddingCode.Core.VisionPipelineException visionEx)
        {
            return VisionProblem(visionEx);
        }

        return Accepted(result);
    }

    /// <summary>ADR-077 §9.1：视觉受理失败返回稳定 4xx + 错误码，不静默丢图。</summary>
    private IActionResult VisionProblem(PuddingCode.Core.VisionPipelineException exception)
    {
        var status = exception.Code switch
        {
            VisionErrorCodes.ArtifactMissing => StatusCodes.Status404NotFound,
            VisionErrorCodes.ArtifactForbidden => StatusCodes.Status403Forbidden,
            VisionErrorCodes.RequestLimitExceeded => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(
            statusCode: status,
            title: exception.Code,
            detail: exception.Message);
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
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        [FromServices] ICreateSteeringHandler handler,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequiredIdError(errors, nameof(conversationId), conversationId, 64);
        AddRequiredIdError(errors, nameof(turnId), turnId, 64);
        AddRequiredIdError(errors, nameof(workspaceId), workspaceId, 64);
        if (string.IsNullOrWhiteSpace(request.Text))
            errors[nameof(request.Text)] = ["Steering text is required."];
        else if (request.Text.Length > 32_768)
            errors[nameof(request.Text)] = ["Steering text cannot exceed 32768 characters."];

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The steering request is invalid.",
            });
        }

        try
        {
            var result = await handler.HandleAsync(
                new CreateSteeringCommand(
                    conversationId,
                    turnId,
                    request.Text,
                    Math.Clamp(request.Priority, 0, 1000),
                    ResolveUserId())
                {
                    WorkspaceId = workspaceId,
                    AgentId = request.AgentId,
                    SourceQueueItemId = request.SourceQueueItemId,
                },
                ct);
            return Accepted(result);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Steering was not accepted",
                detail: ex.Message);
        }
    }

    private string ResolveUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("User identity required.");

    private IActionResult? ValidateSubmitTurn(
        string conversationId,
        string workspaceId,
        SubmitTurnHttpRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        AddRequiredIdError(errors, nameof(conversationId), conversationId, 64);
        AddRequiredIdError(errors, nameof(workspaceId), workspaceId, 64);
        AddRequiredIdError(errors, nameof(request.ClientRequestId), request.ClientRequestId, 64);
        AddRequiredIdError(errors, nameof(request.ClientMessageId), request.ClientMessageId, 64);

        if (!string.Equals(request.Recipients?.Type, "agent", StringComparison.OrdinalIgnoreCase))
        {
            errors["recipients.type"] =
                ["Only explicit agent recipients are supported. Broadcast is not accepted."];
        }

        var agentIds = request.Recipients?.AgentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (agentIds.Length == 0)
            errors["recipients.agentIds"] = ["At least one explicit agent ID is required."];
        else if (agentIds.Any(id => id.Length > 128))
            errors["recipients.agentIds"] = ["Agent IDs cannot exceed 128 characters."];

        if (request.Content is null || request.Content.Count == 0)
        {
            errors[nameof(request.Content)] = ["At least one content part is required."];
        }
        else
        {
            var contentError = ConversationContentValidator.Validate(request.Content);
            if (contentError is not null)
                errors[nameof(request.Content)] = [contentError];
        }

        return errors.Count == 0
            ? null
            : ValidationProblem(
                new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The conversation turn request is invalid.",
                });
    }

    private static void AddRequiredIdError(
        IDictionary<string, string[]> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[field] = [$"{field} is required."];
        else if (value.Length > maxLength)
            errors[field] = [$"{field} cannot exceed {maxLength} characters."];
    }
}

/// <summary>SubmitTurn HTTP DTO — Controller 层专用。</summary>
public sealed record SubmitTurnHttpRequest
{
    public required string ClientRequestId { get; init; }
    public required string ClientMessageId { get; init; }
    public required RecipientRequest Recipients { get; init; }
    public required IReadOnlyList<ContentPart> Content { get; init; }
    /// <summary>附加元数据（如 vision_artifact_id），透传至控制链。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Steering HTTP DTO — Controller 层专用。</summary>
public sealed record SteeringHttpRequest
{
    public required string Text { get; init; }
    public int Priority { get; init; } = 100;
    public string? AgentId { get; init; }
    public string? SourceQueueItemId { get; init; }
}
