using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>Loads the current room participant set from workspace configuration.</summary>
public sealed class WorkspaceRoomParticipantProvider
{
    private readonly IWorkspaceAgentCatalog _agentCatalog;
    private readonly ExternalAgentDirectory? _externalAgents;

    public WorkspaceRoomParticipantProvider(IWorkspaceAgentCatalog agentCatalog, ExternalAgentDirectory? externalAgents = null)
    {
        _agentCatalog = agentCatalog;
        _externalAgents = externalAgents;
    }

    public async Task<IReadOnlyList<RoomParticipant>> GetParticipantsAsync(
        string workspaceId,
        string roomId,
        string userId = "system",
        string? userDisplayName = null,
        CancellationToken ct = default)
    {
        var agents = await _agentCatalog.ListAgentsAsync(workspaceId, ct);

        var participants = WorkspaceRoomParticipantResolver.Resolve(
            workspaceId,
            roomId,
            userId,
            userDisplayName,
            agents);
        if (_externalAgents is null)
            return participants;

        var adapters = await _externalAgents.ListAsync(workspaceId, ct);
        return [..participants, ..adapters.Select(adapter => new RoomParticipant
        {
            ParticipantId = adapter.AgentId,
            RoomId = roomId,
            Kind = MessageEndpointKinds.ExternalAgent,
            EndpointId = adapter.AgentId,
            DisplayName = adapter.Name,
            Status = adapter.Online ? "available" : "offline",
        })];
    }
}
