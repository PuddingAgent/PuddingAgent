using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed record CreateSessionSteeringMessage(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    string MessageText,
    string? SourceQueueItemId,
    string? CreatedBy,
    int Priority = 100);

/// <summary>
/// Durable store for user steering messages that should be injected into a running Agent loop.
/// </summary>
public sealed class SessionSteeringService : ISessionSteeringService
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ILogger<SessionSteeringService> _logger;
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(30);

    public SessionSteeringService(
        IDbContextFactory<PlatformDbContext> dbFactory,
        ILogger<SessionSteeringService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<SessionSteeringMessageEntity> CreateAsync(
        CreateSessionSteeringMessage request,
        CancellationToken ct = default)
    {
        var text = request.MessageText.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Steering message cannot be empty.", nameof(request));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = new SessionSteeringMessageEntity
        {
            SteeringId = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentId = string.IsNullOrWhiteSpace(request.AgentId) ? null : request.AgentId.Trim(),
            SourceQueueItemId = string.IsNullOrWhiteSpace(request.SourceQueueItemId) ? null : request.SourceQueueItemId.Trim(),
            MessageText = text,
            Priority = Math.Clamp(request.Priority, 0, 1000),
            CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? null : request.CreatedBy.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status = SessionSteeringStatuses.Pending,
        };
        db.SessionSteeringMessages.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<ConsumedSessionSteeringMessage?> ConsumeNextAsync(
        string sessionId,
        string? agentId,
        int round,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var now = DateTimeOffset.UtcNow;
        var expiresBefore = now - PendingTtl;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.SessionSteeringMessages
            .Where(m => m.SessionId == sessionId
                && m.Status == SessionSteeringStatuses.Pending)
            .ToListAsync(ct);

        var expired = candidates
            .Where(m => m.CreatedAtUtc < expiresBefore)
            .ToList();
        if (expired.Count > 0)
        {
            foreach (var item in expired)
            {
                item.Status = SessionSteeringStatuses.Expired;
                item.ExpiredAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
        }

        var normalizedAgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
        var pending = candidates
            .Where(m => m.Status == SessionSteeringStatuses.Pending
                && (m.AgentId == null || m.AgentId == normalizedAgentId))
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.CreatedAtUtc)
            .FirstOrDefault();

        if (pending is null)
            return null;

        pending.Status = SessionSteeringStatuses.Consumed;
        pending.ConsumedAtUtc = now;
        pending.ConsumedRound = round;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[SessionSteering] Consumed steering={SteeringId} session={Session} agent={Agent} round={Round}",
            pending.SteeringId, pending.SessionId, pending.AgentId ?? "(any)", round);

        return new ConsumedSessionSteeringMessage(
            pending.SteeringId,
            pending.WorkspaceId,
            pending.SessionId,
            pending.AgentId,
            pending.MessageText,
            pending.Priority,
            pending.CreatedAtUtc,
            now,
            round);
    }
}
