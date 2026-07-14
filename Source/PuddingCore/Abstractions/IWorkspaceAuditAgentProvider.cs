namespace PuddingCode.Abstractions;

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
