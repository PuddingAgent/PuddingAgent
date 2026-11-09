using Pudding.Api.Contracts;

namespace PuddingPlatform.Services.Agents;

public sealed record AgentAuthoringReceipt(string AgentId, long Revision, DateTimeOffset CreatedAt);

/// <summary>Host authoring seam: create/archive through the existing configuration authority (no second store).</summary>
public interface IAgentAuthoringBackend
{
    Task<AgentAuthoringReceipt> CreateAsync(string workspaceId, CreateAgentRequest request,
        CancellationToken ct = default);
    Task<AgentAuthoringReceipt> ArchiveAsync(string workspaceId, string agentId, long expectedRevision,
        CancellationToken ct = default);
}

/// <summary>
/// PA-02 I2 factory bound to one management actor: creation materializes an Agent instance but never starts inference.
/// </summary>
public sealed class PlatformAgentFactoryApi(IAgentAuthoringBackend backend, string actorId) : IAgentFactoryApi
{
    public Task<AgentInstanceReceipt> CreateAsync(CreateAgentRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OperationId)
            || string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.TemplateId)
            || string.IsNullOrWhiteSpace(request.DisplayName))
            throw new AgentDirectoryException("agent.request_invalid", "A bounded create request is required.");
        return CreateCoreAsync(request, ct);
    }

    public Task<AgentInstanceReceipt> ArchiveAsync(ArchiveAgentRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OperationId) || request.Agent is null
            || string.IsNullOrWhiteSpace(request.Agent.WorkspaceId) || string.IsNullOrWhiteSpace(request.Agent.AgentId)
            || request.ExpectedRevision < 0)
            throw new AgentDirectoryException("agent.request_invalid", "A bounded archive request is required.");
        return ArchiveCoreAsync(request, ct);
    }

    private async Task<AgentInstanceReceipt> CreateCoreAsync(CreateAgentRequest request, CancellationToken ct)
    {
        var receipt = await backend.CreateAsync(request.WorkspaceId, request, ct).ConfigureAwait(false);
        return new(request.OperationId, request.WorkspaceId, receipt.AgentId, receipt.Revision, receipt.CreatedAt);
    }

    private async Task<AgentInstanceReceipt> ArchiveCoreAsync(ArchiveAgentRequest request, CancellationToken ct)
    {
        var receipt = await backend.ArchiveAsync(request.Agent.WorkspaceId, request.Agent.AgentId,
            request.ExpectedRevision, ct).ConfigureAwait(false);
        return new(request.OperationId, request.Agent.WorkspaceId, receipt.AgentId, receipt.Revision,
            receipt.CreatedAt);
    }
}
