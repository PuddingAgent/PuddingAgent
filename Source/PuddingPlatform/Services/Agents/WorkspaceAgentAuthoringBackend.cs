using Pudding.Api.Contracts;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Agents;

/// <summary>Adapter over WorkspaceAgentFileService for create/archive; it never starts a model or creates a Work.</summary>
public sealed class WorkspaceAgentAuthoringBackend(WorkspaceAgentFileService agents,
    PuddingCode.Configuration.PuddingDataPaths paths) : IAgentAuthoringBackend
{
    public async Task<AgentAuthoringReceipt> CreateAsync(string workspaceId, CreateAgentRequest request,
        CancellationToken ct = default)
    {
        var created = await agents.CreateAgentAsync(workspaceId, new Data.Dtos.CreateWorkspaceAgentRequest(
            Name: request.DisplayName,
            Description: null,
            DisplayName: request.DisplayName,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: request.TemplateId,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null), ct).ConfigureAwait(false);
        return new(created.AgentId, Revision(created.AgentId), created.CreatedAt);
    }

    public async Task<AgentAuthoringReceipt> ArchiveAsync(string workspaceId, string agentId, long expectedRevision,
        CancellationToken ct = default)
    {
        var archived = await agents.ArchiveAgentAsync(workspaceId, agentId, expectedRevision, ct).ConfigureAwait(false);
        return new(archived.AgentId, Revision(archived.AgentId), DateTimeOffset.UtcNow);
    }

    private long Revision(string agentId)
    {
        var manifest = Path.Combine(paths.AgentInstanceRoot(agentId), "manifest.json");
        return File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest).Ticks : 0;
    }
}
