using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.1：验收合同的持久写入/读取。
/// <para>
/// 一个 (goal_run_id, activation_epoch, objective_version) 只有一行；内容不变时写入是幂等的
/// （不递增版本）；条件或检查定义变化时必须递增 ContractVersion，使旧版本的检查结果失去意义。
/// 空条件允许落库（代表"合同存在但无必需条件"），由 Verifier 按空合同拒绝 vacuous pass。
/// </para>
/// </summary>
public sealed class GoalAcceptanceContractStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    public async Task<GoalAcceptanceContractEntity?> LoadAsync(
        string goalRunId,
        int activationEpoch,
        int objectiveVersion,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GoalAcceptanceContracts.AsNoTracking().SingleOrDefaultAsync(
            item => item.GoalRunId == goalRunId
                && item.ActivationEpoch == activationEpoch
                && item.ObjectiveVersion == objectiveVersion,
            ct);
    }

    /// <summary>
    /// 幂等 upsert：内容与指纹不变则不写库、不递增版本；任一变化则以新版本替换。
    /// </summary>
    public async Task<GoalAcceptanceContractEntity> SaveAsync(
        string goalRunId,
        int activationEpoch,
        int objectiveVersion,
        string? planFingerprint,
        IReadOnlyList<GoalCriterion> criteria,
        IReadOnlyList<GoalCheckSpec> checks,
        string source = "bounded_planning",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(checks);

        var criteriaJson = GoalVerificationPersistence.SerializeCriteria(criteria);
        var checksJson = GoalVerificationPersistence.SerializeChecks(checks);
        var now = DateTimeOffset.UtcNow;
        var contractId = GoalVerificationPersistence.BuildContractId(
            goalRunId,
            activationEpoch,
            objectiveVersion);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GoalAcceptanceContracts.SingleOrDefaultAsync(
            item => item.ContractId == contractId,
            ct);

        if (existing is null)
        {
            var created = new GoalAcceptanceContractEntity
            {
                ContractId = contractId,
                GoalRunId = goalRunId,
                ActivationEpoch = activationEpoch,
                ObjectiveVersion = objectiveVersion,
                ContractVersion = 1,
                PlanFingerprint = planFingerprint,
                CriteriaJson = criteriaJson,
                ChecksJson = checksJson,
                Source = source,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.GoalAcceptanceContracts.Add(created);
            await db.SaveChangesAsync(ct);
            return created;
        }

        var unchanged = string.Equals(existing.CriteriaJson, criteriaJson, StringComparison.Ordinal)
            && string.Equals(existing.ChecksJson, checksJson, StringComparison.Ordinal)
            && string.Equals(existing.PlanFingerprint, planFingerprint, StringComparison.Ordinal);
        if (unchanged)
            return existing;

        existing.ContractVersion += 1;
        existing.PlanFingerprint = planFingerprint;
        existing.CriteriaJson = criteriaJson;
        existing.ChecksJson = checksJson;
        existing.Source = source;
        existing.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return existing;
    }
}
