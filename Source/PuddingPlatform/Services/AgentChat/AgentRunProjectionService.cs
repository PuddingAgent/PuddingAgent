using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Builds Agent contact-list status from the canonical Conversation Event Store.</summary>
public interface IAgentRunProjectionService
{
    Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(
        string workspaceId,
        string ownerUserId,
        CancellationToken ct);
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
    private static readonly string[] LifecycleEventTypes =
    [
        ConversationEventTypes.TurnStarted,
        ConversationEventTypes.MessageStarted,
        ConversationEventTypes.MessageContentAppended,
        ConversationEventTypes.MessageThinkingSummaryAppended,
        ConversationEventTypes.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
        ConversationEventTypes.TurnCompleted,
        ConversationEventTypes.TurnFailed,
        ConversationEventTypes.TurnCancelled,
        ConversationEventTypes.RunLeaseLost,
        ConversationEventTypes.ErrorRecorded,
    ];

    public async Task<IReadOnlyList<AgentStatusProjection>> GetWorkspaceAgentStatusesAsync(
        string workspaceId,
        string ownerUserId,
        CancellationToken ct)
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

        var conversationIds = projectedSessions
            .Select(item => item.Session?.SessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        var eventHeads = await LoadEventHeadsAsync(db, conversationIds, ct);

        return projectedSessions
            .Select(item =>
            {
                var session = item.Session;
                var conversationId = session?.SessionId ?? "";
                eventHeads.TryGetValue(conversationId, out var heads);
                var latestEvent = heads.Latest;
                var latestLifecycleEvent = heads.Lifecycle;
                var status = session is null ? "idle" : MapStatus(session.Status, latestLifecycleEvent);

                return new AgentStatusProjection(
                    workspaceId,
                    ownerUserId,
                    item.Agent.AgentId,
                    conversationId,
                    status,
                    status == "running" ? latestLifecycleEvent?.RunId : null,
                    session?.Title ?? item.Agent.DisplayName ?? item.Agent.Name ?? "",
                    0,
                    latestEvent?.Sequence ?? 0,
                    latestEvent is null
                        ? session?.LastActiveAt ?? item.Agent.UpdatedAt
                        : ParseOccurredAt(latestEvent.OccurredAt));
            })
            .ToList();
    }

    internal sealed record EventHead(long Sequence, string Type, string? RunId, string OccurredAt);

    internal static async Task<Dictionary<string, (EventHead? Latest, EventHead? Lifecycle)>> LoadEventHeadsAsync(
        PlatformDbContext db, IReadOnlyList<string> conversationIds, CancellationToken ct)
    {
        var result = new Dictionary<string, (EventHead?, EventHead?)>(StringComparer.Ordinal);
        foreach (var conversationId in conversationIds.Distinct(StringComparer.Ordinal))
        {
            // One indexed reverse seek per main conversation, usually one row. GroupBy/First
            // translates into ROW_NUMBER over the entire history and reads large payloads
            // on every status poll. Keep only the fields this projection actually consumes.
            var events = db.ConversationEvents.AsNoTracking()
                .Where(e => e.ConversationId == conversationId);
            var latest = await events.OrderByDescending(e => e.Sequence)
                .Select(e => new EventHead(e.Sequence, e.Type, e.RunId, e.OccurredAt))
                .FirstOrDefaultAsync(ct);
            var lifecycle = latest;
            if (latest is not null && !LifecycleEventTypes.Contains(latest.Type))
            {
                lifecycle = await events.Where(e => LifecycleEventTypes.Contains(e.Type))
                    .OrderByDescending(e => e.Sequence)
                    .Select(e => new EventHead(e.Sequence, e.Type, e.RunId, e.OccurredAt))
                    .FirstOrDefaultAsync(ct);
            }
            result[conversationId] = (latest, lifecycle);
        }
        return result;
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

    private static string MapStatus(
        SessionStatus sessionStatus,
        EventHead? latestLifecycleEvent)
    {
        if (sessionStatus == SessionStatus.Frozen)
            return "offline";
        if (latestLifecycleEvent is null)
            return sessionStatus == SessionStatus.Failed ? "failed" : "idle";

        if (IsTerminalEvent(latestLifecycleEvent.Type))
            return "idle";
        if (sessionStatus == SessionStatus.Failed ||
            latestLifecycleEvent.Type == ConversationEventTypes.ErrorRecorded)
        {
            return "failed";
        }
        if (DateTimeOffset.UtcNow - ParseOccurredAt(latestLifecycleEvent.OccurredAt) > ActiveRunStaleAfter)
            return "idle";

        return "running";
    }

    private static bool IsTerminalEvent(string eventType)
        => eventType is
            ConversationEventTypes.TurnCompleted or
            ConversationEventTypes.TurnFailed or
            ConversationEventTypes.TurnCancelled or
            ConversationEventTypes.RunLeaseLost;

    private static DateTimeOffset ParseOccurredAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
}
