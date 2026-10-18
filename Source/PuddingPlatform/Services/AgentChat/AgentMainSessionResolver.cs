using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// Agent 主会话身份解析（阶段 C3b；Plan §5.1）：redirect → Agent Manifest MainSessionId →
/// canonical <c>FindMainAsync</c>。只做有界点查，**不**再加载整个工作区会话列表。
/// <para>工作区/owner 不匹配的候选一律拒绝（fail closed）；解析失败返回 null，由调用方映射 404。</para>
/// </summary>
public sealed class AgentMainSessionResolver(
    ISessionRepository sessionRepository,
    IWorkspaceAgentCatalog workspaceAgentCatalog,
    SessionRedirectStore redirectStore,
    ILogger<AgentMainSessionResolver> logger)
{
    private const string DefaultOwnerUserId = "single-user";

    public async Task<SessionRecord?> ResolveAsync(
        string workspaceId,
        string? ownerUserId,
        string agentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var owner = NormalizeOwnerUserId(ownerUserId);
        var agent = await TryGetAgentAsync(workspaceId, agentId, ct);

        var preferredSessionIds = new List<string>(2);
        var redirectedSessionId = redirectStore.Resolve("main", workspaceId, agentId);
        if (!string.IsNullOrWhiteSpace(redirectedSessionId)
            && !string.Equals(redirectedSessionId, "main", StringComparison.OrdinalIgnoreCase))
        {
            preferredSessionIds.Add(redirectedSessionId);
        }

        if (!string.IsNullOrWhiteSpace(agent?.MainSessionId))
            preferredSessionIds.Add(agent!.MainSessionId!);

        foreach (var sessionId in preferredSessionIds.Distinct(StringComparer.Ordinal))
        {
            var session = await sessionRepository.GetAsync(sessionId, ct);
            if (IsUsable(session, workspaceId, owner))
                return session;
        }

        var fallback = await sessionRepository.FindMainAsync(workspaceId, "agent", agentId, ct);
        if (IsUsable(fallback, workspaceId, owner))
            return fallback;

        logger.LogInformation(
            "[AgentMainSessionResolver] No usable main session workspace={WorkspaceId} agent={AgentId} owner={Owner}",
            workspaceId,
            agentId,
            owner);
        return null;
    }

    private async Task<WorkspaceAgentDto?> TryGetAgentAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct)
    {
        try
        {
            var agents = await workspaceAgentCatalog.ListAgentsAsync(workspaceId, ct);
            return agents.FirstOrDefault(agent =>
                string.Equals(agent.AgentId, agentId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            // Agent Manifest 读取失败不应阻断 redirect/canonical fallback 解析；读取路径保持只读。
            logger.LogWarning(
                ex,
                "[AgentMainSessionResolver] Agent catalog read failed workspace={WorkspaceId} agent={AgentId}",
                workspaceId,
                agentId);
            return null;
        }
    }

    private static bool IsUsable(SessionRecord? session, string workspaceId, string owner)
        => session is not null
           && string.Equals(session.WorkspaceId, workspaceId, StringComparison.Ordinal)
           && string.Equals(
               NormalizeOwnerUserId(session.OwnerUserId),
               owner,
               StringComparison.Ordinal);

    private static string NormalizeOwnerUserId(string? ownerUserId)
        => string.IsNullOrWhiteSpace(ownerUserId) || string.Equals(ownerUserId, "admin", StringComparison.Ordinal)
            ? DefaultOwnerUserId
            : ownerUserId;
}
