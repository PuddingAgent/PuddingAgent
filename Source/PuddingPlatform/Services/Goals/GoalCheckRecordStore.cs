using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §6.2：verification/check 的持久工作项生命周期（pending → leased → finished）。
/// <para>
/// 约束：不持有写事务等待模型或进程；租约带过期时间并可被回收重跑；去重键
/// (scope|criterionRevision|definitionHash|inputFingerprint) 相同则复用执行结果；
/// 只有真正运行出报告的执行器才能把记录置为 finished，未运行一律回到 pending。
/// </para>
/// </summary>
public sealed class GoalCheckRecordStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    private const int MaxLeaseScan = 256;

    /// <summary>
    /// 幂等登记检查工作项。同一去重键只有一行；同一 CheckId 但定义/输入指纹变化时，
    /// 旧报告失效并回到 pending 重跑。
    /// </summary>
    public async Task<int> EnqueueAsync(
        string goalRunId,
        int activationEpoch,
        int iterationNo,
        string scope,
        IReadOnlyList<GoalCheckSpec> checks,
        CancellationToken ct = default)
    {
        if (checks.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var created = 0;

        foreach (var check in checks)
        {
            var dedupKey = GoalVerificationPersistence.BuildDedupKey(
                scope,
                check.CriterionRevision,
                check.DefinitionHash,
                check.InputFingerprint);
            var recordId = GoalVerificationPersistence.BuildCheckRecordId(
                goalRunId,
                activationEpoch,
                iterationNo,
                check.CheckId);

            var existing = await db.GoalCheckRecords.SingleOrDefaultAsync(
                item => item.CheckRecordId == recordId,
                ct);
            if (existing is null)
            {
                if (await db.GoalCheckRecords.AsNoTracking().AnyAsync(
                        item => item.DedupKey == dedupKey,
                        ct))
                {
                    // 同一去重键已在别处登记：复用既有执行结果，不重复执行。
                    continue;
                }

                db.GoalCheckRecords.Add(new GoalCheckRecordEntity
                {
                    CheckRecordId = recordId,
                    DedupKey = dedupKey,
                    GoalRunId = goalRunId,
                    ActivationEpoch = activationEpoch,
                    IterationNo = iterationNo,
                    CheckId = check.CheckId,
                    CriterionId = check.CriterionId,
                    CriterionRevision = check.CriterionRevision,
                    DefinitionHash = check.DefinitionHash,
                    InputFingerprint = check.InputFingerprint,
                    Status = GoalCheckRecordStatuses.Pending,
                    Attempt = 0,
                    Priority = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
                created++;
                continue;
            }

            if (string.Equals(existing.DedupKey, dedupKey, StringComparison.Ordinal))
                continue;

            // 定义或输入指纹变化：旧报告不能再用，回到 pending 等重跑。
            existing.DedupKey = dedupKey;
            existing.CriterionRevision = check.CriterionRevision;
            existing.DefinitionHash = check.DefinitionHash;
            existing.InputFingerprint = check.InputFingerprint;
            existing.Status = GoalCheckRecordStatuses.Pending;
            existing.LeaseOwner = null;
            existing.LeaseUntilUtc = null;
            existing.ReportJson = null;
            existing.FailureCode = null;
            existing.UpdatedAtUtc = now;
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// 认领待执行检查：过期租约先回收为可认领，再按优先级认领。写事务内不做任何等待型工作。
    /// </summary>
    public async Task<IReadOnlyList<GoalCheckRecordEntity>> LeaseAsync(
        string leaseOwner,
        string goalRunId,
        int activationEpoch,
        TimeSpan leaseTtl,
        int take,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (take <= 0)
            return [];

        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite 不支持 DateTimeOffset 排序，先按可比较列取候选再在内存排序。
        var candidates = await db.GoalCheckRecords
            .Where(item => item.GoalRunId == goalRunId
                && item.ActivationEpoch == activationEpoch
                && (item.Status == GoalCheckRecordStatuses.Pending
                    || (item.Status == GoalCheckRecordStatuses.Leased
                        && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))))
            .OrderByDescending(item => item.Priority)
            .Take(Math.Min(MaxLeaseScan, take * 4))
            .ToListAsync(ct);

        var claimed = candidates
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.CheckRecordId, StringComparer.Ordinal)
            .Take(take)
            .ToList();

        foreach (var record in claimed)
        {
            record.Status = GoalCheckRecordStatuses.Leased;
            record.LeaseOwner = leaseOwner;
            record.LeaseUntilUtc = now.Add(leaseTtl);
            record.Attempt += 1;
            record.UpdatedAtUtc = now;
        }

        if (claimed.Count > 0)
            await db.SaveChangesAsync(ct);
        return claimed;
    }

    /// <summary>
    /// 写回检查结果。只有持有租约的执行器可以完成；报告为 pending（未真正运行）时
    /// 记录回到 pending 而不是 finished——不存在"没有运行但已完成的检查"。
    /// </summary>
    public async Task<bool> FinishAsync(
        string checkRecordId,
        string leaseOwner,
        GoalCheckReport report,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.GoalCheckRecords.SingleOrDefaultAsync(
            item => item.CheckRecordId == checkRecordId,
            ct);
        if (record is null)
            return false;
        if (!string.Equals(record.Status, GoalCheckRecordStatuses.Leased, StringComparison.Ordinal))
            return false;
        if (!string.Equals(record.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            return false;

        if (string.Equals(report.Status, GoalCriterionResultStatuses.Pending, StringComparison.Ordinal))
        {
            record.Status = GoalCheckRecordStatuses.Pending;
            record.LeaseOwner = null;
            record.LeaseUntilUtc = null;
            record.ReportJson = null;
            record.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return false;
        }

        record.ReportJson = GoalVerificationPersistence.SerializeReport(report);
        record.Status = GoalCheckRecordStatuses.Finished;
        record.FailureCode = report.FailureCode;
        record.LeaseOwner = null;
        record.LeaseUntilUtc = null;
        record.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<GoalCheckRecordEntity>> ReadForEpochAsync(
        string goalRunId,
        int activationEpoch,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var records = await db.GoalCheckRecords.AsNoTracking()
            .Where(item => item.GoalRunId == goalRunId && item.ActivationEpoch == activationEpoch)
            .ToListAsync(ct);
        return records
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.CheckRecordId, StringComparer.Ordinal)
            .ToList();
    }
}
