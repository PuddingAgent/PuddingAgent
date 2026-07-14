using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// Adapter that bridges Platform's IWorkspaceAgentCatalog to Core's IWorkspaceAgentQueryService.
/// </summary>
public sealed class WorkspaceAgentQueryServiceAdapter : IWorkspaceAgentQueryService
{
    private readonly IWorkspaceAgentCatalog _catalog;

    public WorkspaceAgentQueryServiceAdapter(IWorkspaceAgentCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
    {
        var agents = await _catalog.ListAgentsAsync(workspaceId, ct);
        return agents.Select(a => new AgentInfo
        {
            AgentId = a.AgentId,
            Name = a.Name,
            DisplayName = a.DisplayName,
            SourceTemplateId = a.SourceTemplateId,
            IsEnabled = a.IsEnabled,
        }).ToList();
    }
}
