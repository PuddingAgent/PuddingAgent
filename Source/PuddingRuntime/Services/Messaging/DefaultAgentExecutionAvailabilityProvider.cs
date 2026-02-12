using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Messaging;

/// <summary>
/// Availability provider backed by the runtime agent execution state registry.
/// </summary>
public sealed class DefaultAgentExecutionAvailabilityProvider : IAgentExecutionAvailabilityProvider
{
    private readonly IAgentExecutionStateRegistry _registry;

    public DefaultAgentExecutionAvailabilityProvider(IAgentExecutionStateRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public Task<AgentExecutionAvailability> GetAsync(string workspaceId, string agentId, CancellationToken ct = default) =>
        Task.FromResult(_registry.Get(workspaceId, agentId));
}
