using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingController.Data;
using PuddingController.Data.Entities;

namespace PuddingController.Services;

/// <summary>PostgreSQL 审计事件存储——审计记录持久化到共享数据库。</summary>
public sealed class InMemoryAuditEventStore : IAuditEventStore
{
    private readonly IDbContextFactory<ControllerDbContext> _dbFactory;

    public InMemoryAuditEventStore(IDbContextFactory<ControllerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task RecordAsync(AuditEventRecord record, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventId = record.EventId,
            EventType = record.EventType.ToString(),
            SessionId = record.SessionId,
            MessageId = record.MessageId,
            WorkspaceId = record.WorkspaceId,
            AgentTemplateId = record.AgentTemplateId,
            ApprovalId = record.ApprovalId,
            Detail = record.Detail,
            Timestamp = record.Timestamp,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<AuditEventRecord?> GetAsync(string eventId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = await db.AuditEvents.FindAsync([eventId], ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<AuditEventRecord>> QueryAsync(
        string? sessionId = null,
        string? messageId = null,
        string? workspaceId = null,
        string? approvalId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var q = db.AuditEvents.AsQueryable();
        if (workspaceId is not null) q = q.Where(e => e.WorkspaceId == workspaceId);
        if (sessionId is not null) q = q.Where(e => e.SessionId == sessionId);
        if (messageId is not null) q = q.Where(e => e.MessageId == messageId);
        if (approvalId is not null) q = q.Where(e => e.ApprovalId == approvalId);
        var results = await q.OrderByDescending(e => e.Timestamp).Take(limit).ToListAsync(ct);
        return results.Select(ToRecord).ToList();
    }

    private static AuditEventRecord ToRecord(AuditEventEntity e) => new()
    {
        EventId = e.EventId,
        EventType = Enum.Parse<AuditEventType>(e.EventType),
        SessionId = e.SessionId,
        MessageId = e.MessageId,
        WorkspaceId = e.WorkspaceId,
        AgentTemplateId = e.AgentTemplateId,
        ApprovalId = e.ApprovalId,
        Detail = e.Detail,
        Timestamp = e.Timestamp,
    };
}
