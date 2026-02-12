using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Controllers.Api;

[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/messages")]
public sealed class WorkspaceMessageController(IMessageSystem messageSystem) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<MessageSendResult>> Send(
        string workspaceId,
        [FromBody] WorkspaceMessageSendRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { message = "workspaceId is required." });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { message = "content is required." });

        var targetAgentIds = (request.TargetAgentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var audience = string.IsNullOrWhiteSpace(request.Audience)
            ? (targetAgentIds.Count == 0 ? MessageAudiences.Broadcast : MessageAudiences.Direct)
            : request.Audience.Trim();
        var roomId = string.IsNullOrWhiteSpace(request.RoomId) ? "default" : request.RoomId.Trim();

        if (!string.Equals(audience, MessageAudiences.Broadcast, StringComparison.OrdinalIgnoreCase)
            && targetAgentIds.Count == 0)
        {
            return BadRequest(new { message = "targetAgentIds is required for direct agent messages." });
        }

        IReadOnlyList<MessageAddress> targets = string.Equals(audience, MessageAudiences.Broadcast, StringComparison.OrdinalIgnoreCase)
            ? new List<MessageAddress> { new() { Kind = MessageEndpointKinds.Room, Id = roomId, WorkspaceId = workspaceId } }
            : targetAgentIds
                .Select(agentId => new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = agentId,
                    WorkspaceId = workspaceId,
                })
                .ToList();

        var metadata = new Dictionary<string, string>(request.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "workspace_message_api",
        };
        if (!string.IsNullOrWhiteSpace(request.Intent))
            metadata["intent"] = request.Intent.Trim();

        var result = await messageSystem.SendAsync(new MessageEnvelope
        {
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = ResolveCurrentUserId(),
                WorkspaceId = workspaceId,
                DisplayName = User.Identity?.Name,
            },
            To = targets,
            RoomId = roomId,
            ConversationId = request.ConversationId,
            ReplyToMessageId = request.ReplyToMessageId,
            Audience = audience,
            Visibility = string.IsNullOrWhiteSpace(request.Visibility)
                ? MessageVisibilities.Public
                : request.Visibility.Trim(),
            Content = request.Content.Trim(),
            Priority = request.Priority,
            Metadata = metadata,
        }, ct);

        return Ok(result);
    }

    private string ResolveCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue("sub")
               ?? User.Identity?.Name
               ?? "user";
    }
}

public sealed record WorkspaceMessageSendRequest
{
    public required string Content { get; init; }
    public string? RoomId { get; init; }
    public string? ConversationId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public IReadOnlyList<string>? TargetAgentIds { get; init; }
    public string? Audience { get; init; }
    public string? Visibility { get; init; }
    public string? Intent { get; init; }
    public int Priority { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
