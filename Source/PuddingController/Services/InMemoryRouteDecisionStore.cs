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
}
