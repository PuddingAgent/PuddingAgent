using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatform.Services.AgentWork;

public interface IAgentWorkTargetResolver
{
    Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct);
}

/// <summary>Reuse an existing owned main session; provisioning a new Agent/session is a separate operation.</summary>
public sealed class AgentWorkTargetResolver(IWorkspaceAgentCatalog catalog, AgentMainSessionResolver sessions) : IAgentWorkTargetResolver
{
    public async Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct)
    {
        var agent = (await catalog.ListAgentsAsync(key.Agent.WorkspaceId, ct)).SingleOrDefault(x => x.AgentId == key.Agent.AgentId)
            ?? throw new AgentWorkException("resource.not_found", "Agent was not found in this workspace.");
        if (!agent.IsEnabled || agent.IsFrozen)
            throw new AgentWorkException("agent.unavailable", "Agent is disabled or frozen.");
        var session = await sessions.ResolveAsync(key.Agent.WorkspaceId, key.Caller.ActorId, key.Agent.AgentId, ct);
        if (session is null || session.AgentInstanceId != key.Agent.AgentId || session.SessionRole != SessionRole.Main
            || session.Status is not (SessionStatus.Active or SessionStatus.Idle))
            throw new AgentWorkException("agent.not_ready", "An active main session owned by this caller and Agent is required.");
        return session.SessionId;
    }
}
