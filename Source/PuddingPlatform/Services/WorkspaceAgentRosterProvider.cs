using PuddingCode.Abstractions;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Builds the agent messaging roster from workspace agent configuration.
/// </summary>
public sealed class WorkspaceAgentRosterProvider : IAgentRosterProvider
{
    private readonly IWorkspaceAgentCatalog _agentCatalog;

    public WorkspaceAgentRosterProvider(IWorkspaceAgentCatalog agentCatalog)
    {
        _agentCatalog = agentCatalog;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
        string workspaceId,
        string roomId,
        bool includeBusy,
        bool includeFrozen,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return [];
        }

        var agents = await _agentCatalog.ListAgentsAsync(workspaceId, ct);
        return agents
            .Where(agent => agent.IsEnabled)
            .Where(agent => includeFrozen || !agent.IsFrozen)
            .OrderBy(agent => agent.DisplayName ?? agent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(MapAgent)
            .ToArray();
    }

    private static AgentRosterItem MapAgent(WorkspaceAgentDto agent)
    {
        var capabilities = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.SourceTemplateId))
        {
            capabilities.Add($"template:{agent.SourceTemplateId}");
        }

        if (!string.IsNullOrWhiteSpace(agent.PreferredProviderId))
        {
            capabilities.Add($"provider:{agent.PreferredProviderId}");
        }

        if (!string.IsNullOrWhiteSpace(agent.PreferredModelId))
        {
            capabilities.Add($"model:{agent.PreferredModelId}");
        }

        return new AgentRosterItem(
            AgentId: agent.AgentId,
            DisplayName: agent.DisplayName ?? agent.Name,
            Address: $"agent:{agent.AgentId}",
            Status: agent.IsFrozen ? "frozen" : "idle",
            CanReceiveMessages: !agent.IsFrozen && agent.IsEnabled,
            Capabilities: capabilities,
            CurrentTask: null);
    }
}
