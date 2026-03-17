using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>内存审计事件存储。</summary>
public sealed class InMemoryAuditEventStore : IAuditEventStore
{
    private readonly ConcurrentBag<AuditEventRecord> _events = [];

    public Task RecordAsync(AuditEventRecord record, CancellationToken ct = default)
    {
        _events.Add(record);
        return Task.CompletedTask;
    }

    public Task<AuditEventRecord?> GetAsync(string eventId, CancellationToken ct = default)
    {
        var found = _events.FirstOrDefault(e => e.EventId == eventId);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<AuditEventRecord>> QueryAsync(
        string? sessionId = null,
        string? messageId = null,
        string? workspaceId = null,
        string? approvalId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var q = _events.AsEnumerable();
        if (workspaceId is not null) q = q.Where(e => e.WorkspaceId == workspaceId);
        if (sessionId is not null) q = q.Where(e => e.SessionId == sessionId);
        return Task.FromResult<IReadOnlyList<AuditEventRecord>>(
            q.OrderByDescending(e => e.Timestamp).Take(limit).ToList());
    }
}
