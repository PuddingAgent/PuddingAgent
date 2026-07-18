using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// ADR-059: SQLite-backed Control Inbox — Read-then-Ack protocol.
/// 持久化 Cancel/Steering/Approval 消息。Read 不修改状态。
/// </summary>
public sealed class SqliteControlInbox(
    IServiceScopeFactory scopeFactory,
    ILogger<SqliteControlInbox> logger) : IControlInbox
{
    public async Task<ControlMessageRecord> EnqueueAsync(
        string conversationId, string? turnId, ControlMessageKind kind,
        string payload, string? sourceUserId, int priority, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var maxSeq = await db.ControlMessages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (long?)m.Sequence, ct) ?? 0;
        var seq = maxSeq + 1;

        db.ControlMessages.Add(new ControlMessageEntity
        {
            ControlId = Guid.NewGuid().ToString("N"),
            Sequence = seq,
            ConversationId = conversationId,
            TurnId = turnId,
            Kind = kind.ToString(),
            Payload = payload,
            SourceUserId = sourceUserId,
            Priority = priority,
            Status = "pending",
            CreatedAt = nowMs,
        });
        await db.SaveChangesAsync(ct);

        return Map(conversationId, turnId, kind, payload, sourceUserId, priority, seq, "pending");
    }

    public async Task<IReadOnlyList<ControlMessageRecord>> ReadPendingAsync(
        ExecutionLease lease, long afterSequence, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entities = await db.ControlMessages
            .Where(m => m.ConversationId == lease.ConversationId)
            .Where(m => m.Sequence > afterSequence)
            .Where(m => m.Status == "pending")
            .Where(m => m.TurnId == null || m.TurnId == lease.TurnId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        return entities.Select(e => Map(e)).ToList();
    }

    public async Task AcknowledgeAsync(
        ExecutionLease lease, string controlId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var affected = await db.ControlMessages
            .Where(m => m.ControlId == controlId)
            .Where(m => m.ConversationId == lease.ConversationId)
            .Where(m => m.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, (string)"acknowledged")
                .SetProperty(m => m.ConsumedAt, nowMs)
                .SetProperty(m => m.ConsumedByRunId, lease.RunId),
                ct);

        if (affected == 0)
            logger.LogWarning("[ControlInbox] Ack failed controlId={Id} run={RunId}", controlId, lease.RunId);
    }

    private static ControlMessageRecord Map(ControlMessageEntity e) =>
        new(e.ControlId, e.Sequence, e.ConversationId, e.TurnId,
            Enum.TryParse<ControlMessageKind>(e.Kind, out var k) ? k : ControlMessageKind.Steering,
            e.Payload, e.SourceUserId, e.Priority, e.Status, DateTimeOffset.UtcNow);

    private static ControlMessageRecord Map(
        string convId, string? turnId, ControlMessageKind kind, string payload,
        string? src, int pri, long seq, string status) =>
        new(Guid.NewGuid().ToString("N"), seq, convId, turnId, kind, payload, src, pri, status, DateTimeOffset.UtcNow);
}
