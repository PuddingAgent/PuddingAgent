using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.AgentChat;

public sealed partial class AgentConversationProjectionService
{
    public async Task<ConversationActivityPage> ReadActivityAsync(string workspaceId, string ownerUserId, string agentId,
        ConversationActivityRead read, CancellationToken ct)
    {
        if (read.AfterSequence < 0 || read.ThroughSequence < read.AfterSequence)
            throw new ArgumentOutOfRangeException(nameof(read));
        if (read.Replay && read.ThroughSequence is null)
            throw new ArgumentException("活动重放必须指定固定游标上限。", nameof(read));
        var session = await sessionRepository.GetAsync(read.MainSessionId, ct);
        if (session is null || session.WorkspaceId != workspaceId
            || (session.PrincipalId ?? session.AgentInstanceId) != agentId
            || NormalizeOwnerUserId(session.OwnerUserId) != NormalizeOwnerUserId(ownerUserId))
            throw new InvalidOperationException("会话与角色归属不匹配。");
        var source = db.ConversationEvents.AsNoTracking().Where(e => e.ConversationId == read.MainSessionId);
        var head = await source.MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        var ceiling = Math.Min(head, read.ThroughSequence ?? head);
        // Validate the root execution; a child run must never become the parent output stream.
        var rootExists = await source.AnyAsync(e => e.Type == ConversationEventTypes.TurnStarted
            && e.RunId == read.RunId && e.TurnId == read.TurnId && e.Sequence <= ceiling, ct);
        if (!rootExists || ceiling < read.AfterSequence)
            return new(ceiling, false, true, []);
        var query = source.Where(e => e.Sequence > read.AfterSequence && e.Sequence <= ceiling);
        if (read.Replay) query = query.Where(e => e.TurnId == read.TurnId);
        var events = await query.OrderBy(e => e.Sequence).Take(257).ToListAsync(ct);
        var more = events.Count > 256;
        if (more) events.RemoveAt(events.Count - 1);
        var items = new List<ProcessSummaryItem>();
        var refresh = false;
        foreach (var evt in events)
        {
            var sameTurn = evt.TurnId == read.TurnId;
            var root = sameTurn && evt.RunId == read.RunId;
            if (MapProcessKind(evt.Type) is not null)
            {
                if (sameTurn && (root || SubAgentRunLifecycleEventTypes.Contains(evt.Type))
                    && TryBuildEventProcessItem(evt, out var item)) items.Add(item);
                continue;
            }
            if (read.Replay)
            {
                // Replay is fixed at the snapshot's ceiling. A root terminal event invalidates a stale active snapshot.
                refresh |= root && TerminalEventTypes.Contains(evt.Type);
            }
            else if (evt.Type != ConversationEventTypes.UsageRecorded
                && !evt.Type.StartsWith("subagent.", StringComparison.Ordinal))
                refresh = true; // Lifecycle/unknown changes remain authoritative in the full Core projection.
        }
        return new(more ? events[^1].Sequence : ceiling, more, refresh, items);
    }
}
