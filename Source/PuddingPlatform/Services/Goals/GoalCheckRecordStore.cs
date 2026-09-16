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
        // 批内去重：AnyAsync 看不到本批尚未 SaveChanges 的 Added 实体，同批同键两行会撞
        // UX_goal_check_records_dedup 直接抛异常（该迭代的检查证据永久缺失）。
        var batchKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var check in checks)
        {
            // 去重键按 (goalRunId, activationEpoch) 作用域：epoch 变更后必须重新真实执行。
            var dedupKey = GoalVerificationPersistence.BuildScopedDedupKey(
                goalRunId,
                activationEpoch,
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
                if (!batchKeys.Add(dedupKey))
                {
                    // 同一批内已登记过同键检查：复用同一份执行结果，不重复登记。
                    continue;
                }

                if (await db.GoalCheckRecords.AsNoTracking().AnyAsync(
                        item => item.DedupKey == dedupKey,
                        ct))
                {
                    // 同一去重键已在本 epoch 的别处登记：复用既有执行结果，不重复执行。
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
        // SQLite provider 既不翻译 DateTimeOffset 比较也不翻译其排序：状态过滤下沉到 SQL，
        // 租约过期判定与时间排序一律在内存完成。否则整条查询在编译期抛
        // InvalidOperationException，检查永远无法被认领（真实运行已复现）。
        var candidates = await db.GoalCheckRecords
            .Where(item => item.GoalRunId == goalRunId
                && item.ActivationEpoch == activationEpoch
                && (item.Status == GoalCheckRecordStatuses.Pending
                    || item.Status == GoalCheckRecordStatuses.Leased))
            .OrderByDescending(item => item.Priority)
            .Take(MaxLeaseScan)
            .ToListAsync(ct);

        var claimed = candidates
            .Where(item => IsClaimable(item, now))
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
    /// 可认领：pending 一律可认领；leased 仅在租约缺失或已过期时可回收重跑。
    /// </summary>
    private static bool IsClaimable(GoalCheckRecordEntity item, DateTimeOffset now)
        => string.Equals(item.Status, GoalCheckRecordStatuses.Pending, StringComparison.Ordinal)
            || (string.Equals(item.Status, GoalCheckRecordStatuses.Leased, StringComparison.Ordinal)
                && (item.LeaseUntilUtc is null || item.LeaseUntilUtc <= now));

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

        // waiting（超时 / 未结束后台进程）不是终态：若落成 finished，去重键会阻止重新登记、Lease 又只认
        // pending，该检查在本 epoch 内就永远不会再执行 —— 等待将失去恢复来源。因此与 pending 一样回到
        // 可认领状态，但保留 FailureCode 供 triage。
        if (string.Equals(report.Status, GoalCriterionResultStatuses.Waiting, StringComparison.Ordinal))
        {
            record.Status = GoalCheckRecordStatuses.Pending;
            record.LeaseOwner = null;
            record.LeaseUntilUtc = null;
            record.ReportJson = null;
            record.FailureCode = report.FailureCode;
            record.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return false;
        }

        // evidence_unavailable（退出码不可得 / test 无汇总 / 零执行）不是可用判定：这类失败多源于
        // 外部环境（构建/宿主抖动），是可恢复的。若落成 finished，去重键会在本 epoch 内永久固化这份
        // 无效证据，检查永远不会再执行，外部可恢复的失败将无法自愈（真实事故：GoalRun 7ef90f2c 因
        // 一次外部并发冲突 exit 1 且无测试汇总，iter5–iter8 连续四轮复用同一失败结果空转）。
        // 与上方 waiting 特判同理：回到可认领状态重跑，但保留 FailureCode 供 triage。
        if (report.EvidenceUnavailable)
        {
            record.Status = GoalCheckRecordStatuses.Pending;
            record.LeaseOwner = null;
            record.LeaseUntilUtc = null;
            record.ReportJson = null;
            record.FailureCode = report.FailureCode;
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
