using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>内存路由决策存储——记录每条消息的路由决策。</summary>
public sealed class InMemoryRouteDecisionStore : IRouteDecisionStore
{
    private readonly ConcurrentDictionary<string, RouteDecisionRecord> _decisions = new();

    public Task SaveAsync(RouteDecisionRecord record, CancellationToken ct = default)
    {
        _decisions[record.RouteDecisionId] = record;
        return Task.CompletedTask;
    }

    public Task<RouteDecisionRecord?> GetAsync(string routeDecisionId, CancellationToken ct = default)
        => Task.FromResult(_decisions.GetValueOrDefault(routeDecisionId));

    public Task<RouteDecisionRecord?> GetByMessageAsync(string messageId, CancellationToken ct = default)
    {
        var found = _decisions.Values.FirstOrDefault(d => d.MessageId == messageId);
        return Task.FromResult(found);
    }

    /// <summary>查询路由决策记录（支持按 workspace/session/message 过滤）。</summary>
    public Task<IReadOnlyList<RouteDecisionRecord>> QueryAsync(
        string? workspaceId = null,
        string? sessionId = null,
        string? messageId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var q = _decisions.Values.AsEnumerable();
        if (workspaceId is not null) q = q.Where(d => d.WorkspaceId == workspaceId);
        if (sessionId is not null) q = q.Where(d => d.SessionId == sessionId);
        if (messageId is not null) q = q.Where(d => d.MessageId == messageId);

        return Task.FromResult<IReadOnlyList<RouteDecisionRecord>>(
            q.OrderByDescending(d => d.Timestamp).Take(limit).ToList());
    }
}
