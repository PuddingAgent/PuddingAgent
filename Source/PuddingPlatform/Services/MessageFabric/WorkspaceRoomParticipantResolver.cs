using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>
/// Builds room participants from the current human user and available workspace agents.
/// Users and agents are both first-class message endpoints in the room.
/// </summary>
public static class WorkspaceRoomParticipantResolver
{
    public static IReadOnlyList<RoomParticipant> Resolve(
        string workspaceId,
        string roomId,
        string userId,
        string? userDisplayName,
        IReadOnlyList<WorkspaceAgentDto> agents)
    {
        var result = new List<RoomParticipant>
        {
            new()
            {
                ParticipantId = $"{workspaceId}:{roomId}:user:{userId}",
                RoomId = roomId,
                Kind = MessageEndpointKinds.User,
                EndpointId = userId,
                DisplayName = string.IsNullOrWhiteSpace(userDisplayName) ? userId : userDisplayName,
                CanSend = true,
                CanReceive = true,
                Status = "available",
            },
        };

        result.AddRange(agents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen && !string.IsNullOrWhiteSpace(agent.MainSessionId))
            .Select(agent => new RoomParticipant
            {
                ParticipantId = $"{workspaceId}:{roomId}:agent:{agent.AgentId}",
                RoomId = roomId,
                Kind = MessageEndpointKinds.Agent,
                EndpointId = agent.AgentId,
                DisplayName = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName,
                AvatarUrl = agent.AvatarUrl,
                CanSend = true,
                CanReceive = true,
                Status = "available",
            }));

        return result;
    }
}
