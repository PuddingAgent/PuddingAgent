using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 内存 Session 仓储。
/// </summary>
public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();

    public Task<SessionRecord> CreateAsync(SessionRecord record, CancellationToken ct = default)
    {
        _sessions[record.SessionId] = record;
        return Task.FromResult(record);
    }

    public Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(_sessions.GetValueOrDefault(sessionId));

    public Task<SessionRecord?> FindActiveAsync(string channelId, string ownerUserId, string workspaceId, string agentTemplateId, CancellationToken ct = default)
    {
        var found = _sessions.Values.FirstOrDefault(s =>
            s.ChannelId == channelId &&
            s.OwnerUserId == ownerUserId &&
            s.WorkspaceId == workspaceId &&
            s.AgentTemplateId == agentTemplateId &&
            s.Status == SessionStatus.Active);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<SessionRecord>> QueryAsync(string? channelId = null, string? userId = null, string? workspaceId = null, CancellationToken ct = default)
    {
        var q = _sessions.Values.AsEnumerable();
        if (channelId is not null) q = q.Where(s => s.ChannelId == channelId);
        if (userId is not null) q = q.Where(s => s.OwnerUserId == userId);
        if (workspaceId is not null) q = q.Where(s => s.WorkspaceId == workspaceId);
        return Task.FromResult<IReadOnlyList<SessionRecord>>(q.ToList());
    }

    public Task UpdateAsync(SessionRecord record, CancellationToken ct = default)
    {
        _sessions[record.SessionId] = record;
        return Task.CompletedTask;
    }
}
