using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>Loads the current room participant set from workspace configuration.</summary>
public sealed class WorkspaceRoomParticipantProvider
{
    private readonly IWorkspaceAgentCatalog _agentCatalog;

    public WorkspaceRoomParticipantProvider(IWorkspaceAgentCatalog agentCatalog)
    {
        _agentCatalog = agentCatalog;
    }

    public async Task<IReadOnlyList<RoomParticipant>> GetParticipantsAsync(
        string workspaceId,
        string roomId,
        string userId = "system",
        string? userDisplayName = null,
        CancellationToken ct = default)
    {
        var agents = await _agentCatalog.ListAgentsAsync(workspaceId, ct);

        return WorkspaceRoomParticipantResolver.Resolve(
            workspaceId,
            roomId,
            userId,
            userDisplayName,
            agents);
    }
}
