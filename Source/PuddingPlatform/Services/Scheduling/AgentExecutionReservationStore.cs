using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Scheduling;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

public sealed class AgentExecutionReservationStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<AgentExecutionReservationStore> logger)
    : IAgentExecutionReservationStore
{
    public async Task<AgentReservationResult> TryReserveAsync(
        string workspaceId,
        string agentId,
        string taskId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        Validate(workspaceId, agentId, taskId, ownerId, leaseDuration);
        var now = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ExpireInsideAsync(db, now, ct);

            var existing = await db.AgentExecutionReservations
                .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId
                    && item.AgentId == agentId
                    && item.Status == "active", ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                var kind = existing.TaskId == taskId && existing.OwnerId == ownerId
                    ? AgentReservationResultKind.AlreadyOwned
                    : AgentReservationResultKind.Conflict;
                return new AgentReservationResult(kind, ToSnapshot(existing));
            }

            var taskReservation = await db.AgentExecutionReservations
                .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId
                    && item.TaskId == taskId
                    && item.Status == "active", ct);
            if (taskReservation is not null)
            {
                await tx.CommitAsync(ct);
                return new AgentReservationResult(
                    AgentReservationResultKind.Conflict,
                    ToSnapshot(taskReservation));
            }

            var entity = new AgentExecutionReservationEntity
            {
                ReservationId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                AgentId = agentId,
                TaskId = taskId,
                OwnerId = ownerId,
                Status = "active",
                LeaseUntilUtc = now.Add(leaseDuration),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.AgentExecutionReservations.Add(entity);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "[AgentReservation] acquired workspace={WorkspaceId} agent={AgentId} task={TaskId} reservation={ReservationId} fence={Fence}",
                workspaceId,
                agentId,
                taskId,
                entity.ReservationId,
                entity.FencingToken);
            return new AgentReservationResult(
                AgentReservationResultKind.Acquired,
                ToSnapshot(entity));
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            await using var recovery = await dbFactory.CreateDbContextAsync(ct);
            var conflict = await recovery.AgentExecutionReservations
                .AsNoTracking()
                .Where(item => item.Status == "active"
                    && item.WorkspaceId == workspaceId
                    && (item.AgentId == agentId || item.TaskId == taskId))
                .OrderBy(item => item.FencingToken)
                .FirstOrDefaultAsync(ct);
            if (conflict is null)
                throw;

            logger.LogDebug(
                ex,
                "[AgentReservation] concurrent conflict workspace={WorkspaceId} agent={AgentId} task={TaskId} existing={ReservationId}",
                workspaceId,
                agentId,
                taskId,
                conflict.ReservationId);
            var kind = conflict.AgentId == agentId
                && conflict.TaskId == taskId
                && conflict.OwnerId == ownerId
                ? AgentReservationResultKind.AlreadyOwned
                : AgentReservationResultKind.Conflict;
            return new AgentReservationResult(kind, ToSnapshot(conflict));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> RenewAsync(
        string reservationId,
        long fencingToken,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.AgentExecutionReservations
            .SingleOrDefaultAsync(item => item.ReservationId == reservationId
                && item.FencingToken == fencingToken
                && item.OwnerId == ownerId
                && item.Status == "active", ct);
        if (entity is null || entity.LeaseUntilUtc <= now)
            return false;

        entity.LeaseUntilUtc = now.Add(leaseDuration);
        entity.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ReleaseAsync(
        string reservationId,
        long fencingToken,
        string ownerId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.AgentExecutionReservations
            .Where(item => item.ReservationId == reservationId
                && item.FencingToken == fencingToken
                && item.OwnerId == ownerId
                && item.Status == "active")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "released")
                .SetProperty(item => item.ReleaseReason, reason)
                .SetProperty(item => item.ReleasedAtUtc, now)
                .SetProperty(item => item.UpdatedAtUtc, now), ct);
        return changed == 1;
    }

    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ExpireInsideAsync(db, timeProvider.GetUtcNow(), ct);
    }

    private static async Task<int> ExpireInsideAsync(
        PlatformDbContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // SQLite EF does not translate DateTimeOffset comparisons. Active rows
        // are bounded by the two partial unique indexes, so compare the small
        // active set in memory and persist one atomic SaveChanges batch.
        var active = await db.AgentExecutionReservations
            .Where(item => item.Status == "active")
            .ToListAsync(ct);
        var expired = active.Where(item => item.LeaseUntilUtc <= now).ToArray();
        foreach (var item in expired)
        {
            item.Status = "expired";
            item.ReleaseReason = "lease_expired";
            item.ReleasedAtUtc = now;
            item.UpdatedAtUtc = now;
        }

        if (expired.Length > 0)
            await db.SaveChangesAsync(ct);
        return expired.Length;
    }

    private static AgentExecutionReservationSnapshot ToSnapshot(
        AgentExecutionReservationEntity entity) => new()
    {
        ReservationId = entity.ReservationId,
        WorkspaceId = entity.WorkspaceId,
        AgentId = entity.AgentId,
        TaskId = entity.TaskId,
        GoalRunId = entity.GoalRunId,
        FencingToken = entity.FencingToken,
        Status = entity.Status,
        LeaseUntilUtc = entity.LeaseUntilUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        ReleasedAtUtc = entity.ReleasedAtUtc,
    };

    private static void Validate(
        string workspaceId,
        string agentId,
        string taskId,
        string ownerId,
        TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
    }
}
