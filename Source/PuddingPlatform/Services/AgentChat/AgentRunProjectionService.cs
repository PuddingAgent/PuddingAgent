using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Builds Agent status projections from the existing session read model.</summary>
public interface IAgentRunProjectionService
{
    Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(string workspaceId, string ownerUserId, CancellationToken ct);
}

/// <summary>Default Agent status projection service for the single-user admin chat client.</summary>
public sealed class AgentRunProjectionService(
    PlatformApiClient api,
    WorkspaceAgentFileService workspaceAgentFileService,
    SessionRedirectStore redirectStore,
    PlatformDbContext db) : IAgentRunProjectionService
{
    private const string DefaultOwnerUserId = "single-user";
    private static readonly TimeSpan ActiveRunStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly string[] StatusEventTypes =
    [
        "delta",
        "thinking",
        "tool_call",
        "tool_result",
        "subagent.spawned",
        "subagent.delta",
        "subagent.thinking",
        "subagent.tool_call",
        "subagent.tool_result",
        "subagent.completed",
        "done",
        "error",
        "cancelled",
        "session.closed",
    ];

    public async Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(string workspaceId, string ownerUserId, CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var allWorkspaceSessions = await api.GetSessionsAsync(workspaceId, ct);
        var agents = await workspaceAgentFileService.ListAgentsAsync(workspaceId, ct);
        var projectedSessions = new List<(WorkspaceAgentDto Agent, SessionRecord? Session)>();
        foreach (var agent in agents)
        {
            projectedSessions.Add((agent, await ResolveAgentMainSessionAsync(
                workspaceId,
                ownerUserId,
                agent.AgentId,
                agent.MainSessionId,
                allWorkspaceSessions,
                ct)));
        }

        var sessionIds = projectedSessions
            .Select(item => item.Session?.SessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();
        var latestEvents = sessionIds.Count == 0
            ? new Dictionary<string, SessionEventLogEntity>(StringComparer.Ordinal)
            : await db.SessionEventLogs
                .AsNoTracking()
                .Where(e => sessionIds.Contains(e.SessionId))
                .Where(e => StatusEventTypes.Contains(e.EventType))
                .GroupBy(e => e.SessionId)
                .Select(g => g.OrderByDescending(e => e.SequenceNum).First())
                .ToDictionaryAsync(e => e.SessionId, StringComparer.Ordinal, ct);

        return projectedSessions
            .Select(item =>
            {
                var session = item.Session;
                latestEvents.TryGetValue(session?.SessionId ?? "", out var latestEvent);
                var status = session is null ? "idle" : MapStatus(session.Status, latestEvent);

                return new AgentStatusProjection(
                    workspaceId,
                    ownerUserId,
                    item.Agent.AgentId,
                    session?.SessionId ?? "",
                    status,
                    status is "running" && latestEvent is not null
                        ? latestEvent.ExecutionId ?? $"{session!.SessionId}:{latestEvent.SequenceNum}"
                        : null,
                    session?.Title ?? item.Agent.DisplayName ?? item.Agent.Name ?? "",
                    0,
                    latestEvent?.SequenceNum ?? 0,
                    latestEvent is null ? session?.LastActiveAt ?? item.Agent.UpdatedAt : ParseRecordedAt(latestEvent.RecordedAt));
            })
            .ToList();
    }

    private static string NormalizeOwnerUserId(string? ownerUserId)
        => string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId == "admin"
            ? DefaultOwnerUserId
            : ownerUserId;

    private async Task<SessionRecord?> ResolveAgentMainSessionAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        string? manifestMainSessionId,
        IReadOnlyList<SessionRecord> sessions,
        CancellationToken ct)
    {
        var redirectedSessionId = redirectStore.Resolve("main", workspaceId, agentId);
        var preferredSessionIds = new[]
            {
                string.Equals(redirectedSessionId, "main", StringComparison.OrdinalIgnoreCase) ? null : redirectedSessionId,
                manifestMainSessionId,
            }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>();

        foreach (var preferredSessionId in preferredSessionIds)
        {
            var preferred = sessions.FirstOrDefault(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.Ordinal))
                ?? await api.GetSessionAsync(preferredSessionId, ct);
            if (preferred is not null && string.Equals(preferred.WorkspaceId, workspaceId, StringComparison.Ordinal))
                return preferred;
        }

        return sessions
            .Where(s => s.SessionRole == SessionRole.Main)
            .Where(s => string.Equals(s.PrincipalKind, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(s => string.Equals(s.PrincipalId ?? s.AgentInstanceId ?? s.AgentTemplateId, agentId, StringComparison.Ordinal))
            .Where(s => string.Equals(NormalizeOwnerUserId(s.OwnerUserId), ownerUserId, StringComparison.Ordinal))
            .OrderByDescending(s => s.LastActiveAt)
            .FirstOrDefault();
    }

    private static string MapStatus(SessionStatus status, SessionEventLogEntity? latestEvent)
    {
        if (status == SessionStatus.Failed)
            return "failed";
        if (status == SessionStatus.Frozen)
            return "offline";
        if (latestEvent is null)
            return "idle";
        if (latestEvent.EventType == "error")
            return "failed";

        if (IsStaleRunningEvent(latestEvent))
            return "idle";

        return IsRunningEvent(latestEvent.EventType) ? "running" : "idle";
    }

    private static bool IsStaleRunningEvent(SessionEventLogEntity latestEvent)
    {
        if (!IsRunningEvent(latestEvent.EventType))
            return false;

        return DateTimeOffset.UtcNow - ParseRecordedAt(latestEvent.RecordedAt) > ActiveRunStaleAfter;
    }

    private static bool IsRunningEvent(string eventType)
        => !IsTerminalEvent(eventType);

    private static bool IsTerminalEvent(string eventType)
        => eventType switch
        {
            "done" or "error" or "cancelled" or "session.closed" or
            "context.compaction.completed" or "context.compaction.failed" => true,
            _ => false,
        };

    private static DateTimeOffset ParseRecordedAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
}
