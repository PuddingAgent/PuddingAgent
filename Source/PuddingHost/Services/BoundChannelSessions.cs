using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

public sealed class BoundChannelSessions(ChannelDevelopmentTrust trust, ISessionRepository sessions)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DevelopmentTurn?> ResolveAsync(AgentInstanceManifest agent, ChannelSender? sender,
        string room, string message, string actor, CancellationToken ct)
    {
        if (sender == null) return null;
        var user = await trust.ResolveAsync(agent.WorkspaceId, agent.AgentInstanceId, sender, ct);
        if (user == null) return null;
        var session = ChannelDevelopmentTrust.PrivateSession(agent.WorkspaceId, agent.AgentInstanceId, sender, user, room);
        await gate.WaitAsync(ct);
        try
        {
            var existing = await sessions.GetAsync(session, ct);
            if (existing == null)
                await sessions.CreateAsync(new SessionRecord
                {
                    WorkspaceId = agent.WorkspaceId, AgentInstanceId = agent.AgentInstanceId, AgentTemplateId = agent.TemplateId,
                    ChannelId = sender.Channel, SessionId = session, OwnerUserId = user, PrincipalKind = "bound-channel",
                    PrincipalId = user, SessionRole = SessionRole.Branch, Title = $"渠道开发会话 · {user}",
                }, ct);
            else if (existing.OwnerUserId != user || existing.PrincipalKind != "bound-channel" ||
                     existing.AgentInstanceId != agent.AgentInstanceId || existing.WorkspaceId != agent.WorkspaceId ||
                     existing.ChannelId != sender.Channel || existing.Status != SessionStatus.Active)
                throw new UnauthorizedAccessException("Channel session binding mismatch.");
        }
        finally { gate.Release(); }
        return new(agent.WorkspaceId, agent.AgentInstanceId, session, message, actor, user, sender);
    }
}
