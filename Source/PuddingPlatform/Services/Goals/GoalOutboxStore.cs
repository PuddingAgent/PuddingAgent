using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>Goal continuation worker 使用的 durable outbox 领取、租约与恢复原语。</summary>
public sealed class GoalOutboxStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    public async Task<IReadOnlyList<GoalOutboxEntity>> PeekDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite 不稳定翻译 DateTimeOffset 比较；pending 集合有界，due/order 在内存完成。
        var candidates = await db.GoalOutbox.AsNoTracking()
            .Where(item => item.Kind == GoalOutboxValues.Continuation
                && item.Status == GoalOutboxValues.Pending)
            .ToListAsync(ct);
        return candidates
            .Where(item => item.DueAtUtc <= now)
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 64))
            .ToList();
    }

    /// <summary>
    /// 单条 CAS 领取。只有 pending 可领取；fencing_token 每次领取递增，旧 Worker
    /// 即使在租约过期后迟到，也无法通过 Acceptance 的最终 fence。
    /// </summary>
    public async Task<GoalOutboxEntity?> TryClaimAsync(
        string outboxId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.GoalOutbox
            .Where(item => item.OutboxId == outboxId
                && item.Kind == GoalOutboxValues.Continuation
                && item.Status == GoalOutboxValues.Pending)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, GoalOutboxValues.Leased)
                .SetProperty(item => item.LeaseOwner, workerId)
                .SetProperty(item => item.LeaseUntilUtc, now.Add(leaseDuration))
                .SetProperty(item => item.FencingToken, item => item.FencingToken + 1)
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                .SetProperty(item => item.LastError, (string?)null), ct);
        if (affected != 1)
            return null;

        return await db.GoalOutbox.AsNoTracking()
            .SingleAsync(item => item.OutboxId == outboxId, ct);
    }

    public async Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var leased = await db.GoalOutbox
            .Where(item => item.Kind == GoalOutboxValues.Continuation
                && item.Status == GoalOutboxValues.Leased)
            .ToListAsync(ct);
        var expired = leased
            .Where(item => item.LeaseUntilUtc is null || item.LeaseUntilUtc <= now)
            .ToList();
        foreach (var item in expired)
        {
            item.Status = GoalOutboxValues.Pending;
            item.LeaseOwner = null;
            item.LeaseUntilUtc = null;
            item.LastError = "lease_expired_recovered";
        }

        if (expired.Count > 0)
            await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    public Task<bool> DeferAsync(
        GoalOutboxEntity lease,
        DateTimeOffset dueAtUtc,
        string reason,
        CancellationToken ct = default)
        => ReleaseLeaseAsync(
            lease,
            GoalOutboxValues.Pending,
            dueAtUtc,
            reason,
            completedAtUtc: null,
            ct);

    public Task<bool> SuppressAsync(
        GoalOutboxEntity lease,
        string reason,
        CancellationToken ct = default)
        => ReleaseLeaseAsync(
            lease,
            GoalOutboxValues.Cancelled,
            lease.DueAtUtc,
            reason,
            DateTimeOffset.UtcNow,
            ct);

    public Task<bool> RetryOrDeadLetterAsync(
        GoalOutboxEntity lease,
        DateTimeOffset retryAtUtc,
        int maxAttempts,
        string error,
        CancellationToken ct = default)
        => ReleaseLeaseAsync(
            lease,
            lease.AttemptCount >= maxAttempts
                ? GoalOutboxValues.DeadLettered
                : GoalOutboxValues.Pending,
            retryAtUtc,
            error,
            lease.AttemptCount >= maxAttempts ? DateTimeOffset.UtcNow : null,
            ct);

    public async Task<GoalOutboxEntity?> GetAsync(
        string outboxId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GoalOutbox.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OutboxId == outboxId, ct);
    }

    private async Task<bool> ReleaseLeaseAsync(
        GoalOutboxEntity lease,
        string targetStatus,
        DateTimeOffset dueAtUtc,
        string reason,
        DateTimeOffset? completedAtUtc,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.GoalOutbox
            .Where(item => item.OutboxId == lease.OutboxId
                && item.Status == GoalOutboxValues.Leased
                && item.LeaseOwner == lease.LeaseOwner
                && item.FencingToken == lease.FencingToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, targetStatus)
                .SetProperty(item => item.DueAtUtc, dueAtUtc)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.LastError, reason)
                .SetProperty(item => item.CompletedAtUtc, completedAtUtc), ct);
        return affected == 1;
    }
}
