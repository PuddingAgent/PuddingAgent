using System.Collections.Concurrent;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>In-memory store for automatic approval audit events.</summary>
public sealed class InMemoryToolApprovalAuditStore : IToolApprovalAuditStore
{
    private readonly ConcurrentDictionary<string, ToolApprovalAuditEvent> _events = new(StringComparer.Ordinal);

    public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
    {
        _events[auditEvent.EventId] = auditEvent;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ToolApprovalAuditEvent> events = _events.Values
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToArray();
        return Task.FromResult(events);
    }
}
