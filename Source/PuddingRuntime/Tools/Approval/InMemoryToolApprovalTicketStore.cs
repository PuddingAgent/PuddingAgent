using System.Collections.Concurrent;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>In-memory store for automatic approval tickets.</summary>
public sealed class InMemoryToolApprovalTicketStore : IToolApprovalTicketStore
{
    private readonly ConcurrentDictionary<string, ToolApprovalTicketRecord> _tickets = new(StringComparer.Ordinal);

    public Task SaveAsync(ToolApprovalTicketRecord ticket, CancellationToken ct = default)
    {
        _tickets[ticket.TicketId] = ticket;
        return Task.CompletedTask;
    }

    public Task<ToolApprovalTicketRecord?> GetAsync(string ticketId, CancellationToken ct = default)
    {
        _tickets.TryGetValue(ticketId, out var ticket);
        return Task.FromResult(ticket);
    }

    public Task<IReadOnlyList<ToolApprovalTicketRecord>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ToolApprovalTicketRecord> tickets = _tickets.Values
            .OrderBy(t => t.CreatedAtUtc)
            .ToArray();
        return Task.FromResult(tickets);
    }
}
