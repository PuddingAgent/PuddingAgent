using PuddingAgents;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingPlatform.Services.Agents;

/// <summary>
/// PA-02 I0 public directory bound to one host-authorized actor. Listing/reading uses AgentReadBinding (no Work/root
/// budget required); it never mutates, starts inference or charges.
/// </summary>
public sealed class PlatformAgentDirectoryApi(IAgentDirectoryStore store, string actorId) : IAgentDirectoryApi
{
    public async Task<AgentDirectoryPage> ListAsync(AgentListQuery query, CancellationToken ct = default)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.WorkspaceId))
            throw new AgentDirectoryException(AgentErrorCodes.RequestInvalid, "A bounded directory query is required.");
        try
        {
            var page = await new AgentDirectoryService(store)
                .ListAsync(new AgentReadBinding(query.WorkspaceId, actorId), query, ct).ConfigureAwait(false);
            return new(page.Agents, page.Cursor, page.Complete);
        }
        catch (AgentBackendException ex)
        {
            throw new AgentDirectoryException(ex.Code, ex.Message);
        }
    }

    public async Task<AgentDirectoryEntry?> GetAsync(AgentRef agent, CancellationToken ct = default)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.WorkspaceId) || string.IsNullOrWhiteSpace(agent.AgentId))
            throw new AgentDirectoryException(AgentErrorCodes.RequestInvalid, "A bounded agent reference is required.");
        try
        {
            return await new AgentDirectoryService(store)
                .GetAsync(new AgentReadBinding(agent.WorkspaceId, actorId), agent, ct).ConfigureAwait(false);
        }
        catch (AgentBackendException ex)
        {
            throw new AgentDirectoryException(ex.Code, ex.Message);
        }
    }
}
