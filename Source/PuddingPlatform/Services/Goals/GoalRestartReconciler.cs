using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Goals;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-074 §12（故障与恢复）：Core 重启后保留 Goal 与证据，但把原 active Goal
/// 投影为 paused/disarmed；只有显式 /goal resume 才生成新 activation epoch 并恢复。
/// 在 PuddingApplicationInitializer 中 schema bootstrap 之后、workers 启动之前执行。
/// </summary>
public sealed class GoalRestartReconciler(
    PlatformDbContext db,
    GoalRunStore store,
    ILogger<GoalRestartReconciler> logger)
{
    /// <summary>返回被 disarm 的 active Goal 数量。幂等：paused/blocked/终态不受影响。</summary>
    public async Task<int> DisarmActiveGoalsAsync(string bootId, CancellationToken ct = default)
    {
        var activeGoals = await db.GoalRuns
            .AsNoTracking()
            .Where(g => g.Status == GoalPhase.Active)
            .Select(g => g.GoalRunId)
            .ToListAsync(ct);

        var disarmed = 0;
        foreach (var goalRunId in activeGoals)
        {
            // bootId 记入 activation_boot_id：重启 disarm 的事实锚点。
            var (mutated, _) = await store.TryMutateAsync(
                goalRunId,
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

        return disarmed;
    }
}
