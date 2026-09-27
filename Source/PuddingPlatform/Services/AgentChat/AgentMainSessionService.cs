using PuddingCode.Platform;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>In-process role main-session entry; persistence and redirects remain owned by Core.</summary>
public sealed class AgentMainSessionService(
    ISessionRepository sessions, WorkspaceAgentFileService agents, SessionRedirectStore redirects)
{
    private static readonly SemaphoreSlim CreationGate = new(1, 1);
    public async Task<SessionRecord> EnsureAsync(string workspaceId, string agentId, string ownerUserId, CancellationToken ct)
    {
        await CreationGate.WaitAsync(ct);
        try
        {
            var agent = await agents.GetAgentAsync(workspaceId, agentId, ct)
                ?? throw new KeyNotFoundException("角色不存在。");
            if (!agent.IsEnabled || agent.IsFrozen) throw new InvalidOperationException("角色已停用或冻结。");
            var redirected = redirects.Resolve("main", workspaceId, agentId);
            var preferred = redirected != "main" ? redirected : agent.MainSessionId;
            var session = string.IsNullOrWhiteSpace(preferred) ? null : await sessions.GetAsync(preferred, ct);
            if (session is not null && session.WorkspaceId != workspaceId) throw new InvalidOperationException("主会话归属不匹配。");
            session ??= await sessions.FindMainAsync(workspaceId, "agent", agentId, ct);
            if (session is null)
                session = await sessions.CreateAsync(new SessionRecord
                {
                    SessionId = Guid.NewGuid().ToString("N"), WorkspaceId = workspaceId,
                    AgentInstanceId = agentId,
                    AgentTemplateId = string.IsNullOrWhiteSpace(agent.SourceTemplateId) ? $"global:{agentId}" : agent.SourceTemplateId,
                    OwnerUserId = ownerUserId, ChannelId = "admin", SessionType = SessionType.ServiceSession,
                    SessionRole = SessionRole.Main, PrincipalKind = "agent", PrincipalId = agentId,
                    Status = SessionStatus.Active, Title = agent.DisplayName ?? agent.Name
                }, ct);
            await agents.SetAgentMainSessionAsync(workspaceId, agentId, session.SessionId, ct);
            return session;
        }
        finally { CreationGate.Release(); }
    }
}
