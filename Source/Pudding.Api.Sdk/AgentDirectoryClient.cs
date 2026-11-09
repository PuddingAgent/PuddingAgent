using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>PA-02 directory facade: borrowed handles only; listing never creates or starts an agent.</summary>
public sealed class AgentDirectoryClient(IAgentDirectoryApi api)
{
    public Task<AgentDirectoryPage> ListAsync(AgentListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return api.ListAsync(query, ct);
    }

    public Task<AgentDirectoryEntry?> GetAsync(AgentRef agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return api.GetAsync(agent, ct);
    }
}
