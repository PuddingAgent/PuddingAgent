using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingController.Data;
using PuddingController.Data.Entities;

namespace PuddingController.Services;

/// <summary>PostgreSQL 路由决策存储——记录每条消息的路由决策并持久化。</summary>
public sealed class InMemoryRouteDecisionStore : IRouteDecisionStore
{
    private readonly IDbContextFactory<ControllerDbContext> _dbFactory;

    public InMemoryRouteDecisionStore(IDbContextFactory<ControllerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveAsync(RouteDecisionRecord record, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = await db.RouteDecisions.FindAsync([record.RouteDecisionId], ct);
        if (existing is null)
            db.RouteDecisions.Add(ToEntity(record));
        else
            db.Entry(existing).CurrentValues.SetValues(ToEntity(record));
        await db.SaveChangesAsync(ct);
    }

    public async Task<RouteDecisionRecord?> GetAsync(string routeDecisionId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = await db.RouteDecisions.FindAsync([routeDecisionId], ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<RouteDecisionRecord?> GetByMessageAsync(string messageId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = await db.RouteDecisions.FirstOrDefaultAsync(d => d.MessageId == messageId, ct);
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>查询路由决策记录（支持按 workspace/session/message 过滤）。</summary>
    public async Task<IReadOnlyList<RouteDecisionRecord>> QueryAsync(
        string? workspaceId = null,
        string? sessionId = null,
        string? messageId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var q = db.RouteDecisions.AsQueryable();
        if (workspaceId is not null) q = q.Where(d => d.WorkspaceId == workspaceId);
        if (sessionId is not null) q = q.Where(d => d.SessionId == sessionId);
        if (messageId is not null) q = q.Where(d => d.MessageId == messageId);
        var results = await q.OrderByDescending(d => d.Timestamp).Take(limit).ToListAsync(ct);
        return results.Select(ToRecord).ToList();
    }

    private static RouteDecisionRecord ToRecord(RouteDecisionEntity e) => new()
    {
        RouteDecisionId = e.RouteDecisionId,
        MessageId = e.MessageId,
        ChannelId = e.ChannelId,
        WorkspaceId = e.WorkspaceId,
        AgentTemplateId = e.AgentTemplateId,
        SessionId = e.SessionId,
        IsSuccess = e.IsSuccess,
        FailureReason = e.FailureReason,
        Timestamp = e.Timestamp,
    };

    private static RouteDecisionEntity ToEntity(RouteDecisionRecord r) => new()
    {
        RouteDecisionId = r.RouteDecisionId,
        MessageId = r.MessageId,
        ChannelId = r.ChannelId,
        WorkspaceId = r.WorkspaceId,
        AgentTemplateId = r.AgentTemplateId,
        SessionId = r.SessionId,
        IsSuccess = r.IsSuccess,
        FailureReason = r.FailureReason,
        Timestamp = r.Timestamp,
    };
}
