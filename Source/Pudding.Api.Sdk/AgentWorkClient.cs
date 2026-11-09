using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Caller-owned API/transport. Binding a handle does not create an Agent, start Core or allocate an LLM.</summary>
public sealed class AgentWorkClient(IAgentWorkApi api)
{
    public AgentWork For(AgentRef agent) => new(api, agent);
}

public static class AgentWorkExtensions
{
    /// <summary>Add execution to an existing Agent handle without changing its configuration API.</summary>
    public static AgentWork Work(this Agent agent, IAgentWorkApi api) => new(api, agent.Reference);
}

public sealed class AgentWork(IAgentWorkApi api, AgentRef agent)
{
    public Task<AgentWorkReceipt> SubmitAsync(AgentWorkRequest request, CancellationToken ct = default) => api.SubmitAsync(agent, request, ct);
    public Task<AgentWorkSnapshot?> GetAsync(string operationId, CancellationToken ct = default) => api.GetAsync(agent, operationId, ct);
}
