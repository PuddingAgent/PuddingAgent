using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Goals;

/// <summary>ADR-092: 重启 Reconciler 的处理计数（disarm 与 auto-resume 分开统计）。</summary>
public sealed record GoalRestartReconcileResult(int DisarmedCount, int AutoResumedCount);

/// <summary>
/// ADR-074 §12（故障与恢复）+ ADR-092（持久 ResumePolicy）：Core 重启后按
/// goal_runs.resume_policy 分流 ——
/// "paused"（默认）：保持历史行为，Active → Paused（core_restart_disarm）；
/// "auto_resume_on_restart"：保持 Active，但 ActivationEpoch++ 且 ActivationBootId=bootId
/// （换发 fence，使旧 writer 失效），并落 goal.resumed 事件（复用既有事件类型，不新增）。
/// 在 PuddingApplicationInitializer 中 schema bootstrap 之后、workers 启动之前执行。
/// </summary>
public sealed class GoalRestartReconciler(
    PlatformDbContext db,
    GoalRunStore store,
    IOptions<GoalRunOptions> options,
    ILogger<GoalRestartReconciler> logger)
{
    /// <summary>幂等：非 Active 不动；auto-resume 同一 bootId 重放不重复递增 epoch。</summary>
    public async Task<GoalRestartReconcileResult> DisarmActiveGoalsAsync(
        string bootId, CancellationToken ct = default)
    {
        var goalOptions = options.Value;
        var activeGoals = await db.GoalRuns
            .AsNoTracking()
            .Where(g => g.Status == GoalPhase.Active)
            .OrderBy(g => g.GoalRunId)
            .Select(g => new { g.GoalRunId, g.ResumePolicy })
            .ToListAsync(ct);

        var disarmed = 0;
        var autoResumed = 0;
        foreach (var goal in activeGoals)
        {
            var policy = NormalizePolicy(goal.ResumePolicy, goalOptions.DefaultResumePolicy);
            if (policy == GoalResumePolicies.AutoResumeOnRestart
                && autoResumed >= goalOptions.MaxAutoResumesPerBoot)
            {
                // 防恢复风暴：超出单次 boot 配额的部分按 paused 处理。
                logger.LogWarning(
                    "[GoalRestart] Auto-resume quota ({Limit}) exhausted; goal={GoalRunId} falls back to paused",
                    goalOptions.MaxAutoResumesPerBoot, goal.GoalRunId);
                policy = GoalResumePolicies.Paused;
            }

            if (policy == GoalResumePolicies.AutoResumeOnRestart)
            {
                // ADR-092：保持 Active，换发 activation fence（epoch++ / bootId），落 goal.resumed。
                var (resumed, _) = await store.TryMutateAsync(
                    goal.GoalRunId,
                    expectedVersion: 0,
                    g =>
                    {
                        // 事务内卫兵：非 Active 或同 boot 重放（fence 已是本 boot）时跳过提交。
                        if (g.Status != GoalPhase.Active || g.ActivationBootId == bootId)
                            return false;
                        g.ActivationBootId = bootId;
                        g.ActivationEpoch++;
                        return true;
                    },
                    new GoalRunStore.GoalEventAppend(
                        GoalEventTypes.Resumed,
                        new { reason = "core_restart_auto_resume", bootId }),
                    traceId: $"goal-restart-{bootId}",
                    ct: ct);

                if (resumed is not null)
                {
                    autoResumed++;
                    logger.LogInformation(
                        "[GoalRestart] Auto-resumed goal={GoalRunId} boot={BootId} epoch={Epoch} -> active",
                        resumed.GoalRunId, bootId, resumed.ActivationEpoch);
                }
            }
            else
            {
                var (mutated, _) = await store.TryMutateAsync(
                    goal.GoalRunId,
                    expectedVersion: 0,
                    g =>
                    {
                        // 事务内卫兵：并发变化时跳过提交（不递增 version、不写事件）。
                        if (g.Status != GoalPhase.Active)
                            return false;
                        g.Status = GoalPhase.Paused;
                        g.StatusReason = "core_restart_disarm";
                        g.ActivationBootId = bootId;
                        g.ActivationEpoch++;
                        return true;
                    },
                    new GoalRunStore.GoalEventAppend(
                        GoalEventTypes.Paused,
                        new { reason = "core_restart_disarm", bootId }),
                    traceId: $"goal-restart-{bootId}",
                    ct: ct);

                if (mutated is not null)
                {
                    disarmed++;
                    logger.LogInformation(
                        "[GoalRestart] Disarmed active goal={GoalRunId} boot={BootId} -> paused",
                        mutated.GoalRunId, bootId);
                }
            }
        }

        return new GoalRestartReconcileResult(disarmed, autoResumed);
    }

    /// <summary>未知/空策略 fail-safe 回落配置默认（默认 paused，等价历史行为）。</summary>
    private static string NormalizePolicy(string? resumePolicy, string defaultPolicy)
        => resumePolicy == GoalResumePolicies.AutoResumeOnRestart
            ? GoalResumePolicies.AutoResumeOnRestart
            : resumePolicy == GoalResumePolicies.Paused
                ? GoalResumePolicies.Paused
                : defaultPolicy;
}
