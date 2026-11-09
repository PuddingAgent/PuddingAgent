using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Does not own or start Core. The caller owns the supplied API/HTTP transport lifetime.</summary>
public sealed class PuddingClient(IAgentConfigurationApi api)
{
    /// <summary>Constructs a resource handle without I/O. Agent.GetAsync verifies existence and authorization.</summary>
    public Workspace Workspace(string workspaceId) => new(api, workspaceId);
}

public sealed class Workspace
{
    internal Workspace(IAgentConfigurationApi api, string id) { Id = id; Agents = new(api, id); }
    public string Id { get; }
    public AgentDirectory Agents { get; }
}

public sealed class AgentDirectory(IAgentConfigurationApi api, string workspaceId)
{
    public async Task<Agent> GetAsync(string agentId, CancellationToken ct = default)
    {
        var reference = new AgentRef(workspaceId, agentId);
        await api.GetAsync(reference, ct);
        return new(api, reference);
    }
}

public sealed class Agent
{
    internal Agent(IAgentConfigurationApi api, AgentRef reference) { Reference = reference; Configuration = new(api, reference); }
    public AgentRef Reference { get; }
    public AgentConfiguration Configuration { get; }
}

public sealed class AgentConfiguration(IAgentConfigurationApi api, AgentRef agent)
{
    public Task<AgentConfigurationSnapshot> GetAsync(CancellationToken ct = default) => api.GetAsync(agent, ct);
    /// <summary>Keep OperationId and the full request unchanged when retrying after an uncertain response.</summary>
    public Task<ConfigurationChangeReceipt> UpdateAsync(AgentConfigurationPatch patch,
        ConfigurationUpdateOptions options, CancellationToken ct = default) => api.UpdateAsync(agent, patch, options, ct);
    public Task<ConfigurationChangePage> ReadChangesAsync(long afterSequence = 0, int limit = 100,
        CancellationToken ct = default) => api.ReadChangesAsync(agent, afterSequence, limit, ct);
}
