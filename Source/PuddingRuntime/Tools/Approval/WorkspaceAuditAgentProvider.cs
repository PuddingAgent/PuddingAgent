using PuddingPlatform.Services;

namespace PuddingRuntime.Services.Tools;

/// <summary>Provides the workspace-scoped Audit agent used by automatic tool approval.</summary>
public interface IWorkspaceAuditAgentProvider
{
    Task<WorkspaceAuditAgentProfile?> FindFirstEnabledAuditAgentAsync(
        string workspaceId,
        CancellationToken ct = default);
}

/// <summary>Resolved Audit agent identity and LLM route for a workspace approval review.</summary>
public sealed record WorkspaceAuditAgentProfile
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? ProviderId { get; init; }
    public string? ProfileId { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>File-backed adapter from workspace agent configuration to the approval reviewer boundary.</summary>
public sealed class WorkspaceAuditAgentProvider : IWorkspaceAuditAgentProvider
{
    private readonly WorkspaceAgentFileService _workspaceAgents;

    public WorkspaceAuditAgentProvider(WorkspaceAgentFileService workspaceAgents)
    {
        _workspaceAgents = workspaceAgents;
    }

    public async Task<WorkspaceAuditAgentProfile?> FindFirstEnabledAuditAgentAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        var candidate = await _workspaceAgents.FindFirstEnabledAuditAgentAsync(workspaceId, ct);
        if (candidate is null)
            return null;

        return new WorkspaceAuditAgentProfile
        {
            WorkspaceId = candidate.WorkspaceId,
            AgentInstanceId = candidate.AgentInstanceId,
            AgentTemplateId = candidate.AgentTemplateId,
            ProviderId = candidate.ProviderId,
            ProfileId = candidate.ProfileId,
            ModelId = candidate.ModelId,
        };
    }
}
