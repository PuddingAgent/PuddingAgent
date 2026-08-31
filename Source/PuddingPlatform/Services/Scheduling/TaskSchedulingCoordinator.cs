using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 事件驱动协调器：消费 task_scheduler_intents → 即时候选评估 →（authoritative）派发启动。
/// <para>
/// 仅在 TaskAutoDispatch.Enabled 且 Mode=authoritative 时启动（与 Worker 的 authoritative
/// 校验一致；shadow 模式由桥跳过入队、Worker 5m 扫描兜底）。同一轮领到的 intents 按
/// workspace 分组合并成一次 evaluator 评估；goal 终态 intent（conversation_events 来源）
/// 先显式重建 availability 投影再评估。崩溃恢复依赖 Dequeue 的过期 lease 回收。
/// </para>
/// </summary>
public sealed class TaskSchedulingCoordinator(
    ITaskSchedulerIntentStore intentStore,
    ITaskAutoDispatchEvaluator evaluator,
    ITaskAutoDispatchStarter starter,
    IWorkspaceAgentCatalog agentCatalog,
    IAgentAvailabilityProjectionStore availabilityStore,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<TaskSchedulingCoordinator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            try
            {
                if (current.Enabled
                    && current.EventDrivenEnabled
                    && string.Equals(current.Mode, "authoritative", StringComparison.OrdinalIgnoreCase))
                    await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[TaskSchedulingCoordinator] round failed; retry next interval");
            }

            try
            {
                await Task.Delay(
                    current.IntentPollInterval <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(2)
                        : current.IntentPollInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>执行一轮 intent 消费（测试入口；ExecuteAsync 循环调用同一方法）。</summary>
    public async Task ProcessOnceAsync(CancellationToken ct = default)
    {
        var current = options.CurrentValue;
        if (!current.Enabled
            || !current.EventDrivenEnabled
            || !string.Equals(current.Mode, "authoritative", StringComparison.OrdinalIgnoreCase))
            return;
        var startedTotal = 0;
        var completedTotal = 0;
        var deadTotal = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var workspaceId in (current.WorkspaceIds ?? [])
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Where(value => !(current.PausedWorkspaceIds ?? []).Contains(value, StringComparer.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var intents = await intentStore.DequeueAsync(
                workspaceId,
                Math.Clamp(current.IntentBatchSize, 1, 500),
                current.IntentLease,
                timeProvider.GetUtcNow(),
                ct);
            if (intents.Count == 0)
                continue;

            try
            {
                // goal 终态事件改变 Agent 可用性事实：先从已提交事实重建投影再评估。
                if (intents.Any(item => item.Source == TaskSchedulerIntentSources.ConversationEvents))
                {
                    var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
                    foreach (var agent in agents.OrderBy(item => item.AgentId, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        await availabilityStore.RebuildAsync(workspaceId, agent.AgentId, ct);
                    }
                }

                var decisions = await evaluator.EvaluateAsync(
                    workspaceId,
                    Math.Clamp(current.CandidateLimit, 1, 500),
                    ct);
                var started = await starter.DispatchAsync(decisions, ct);
                startedTotal += started;
                foreach (var intent in intents)
                {
                    await intentStore.CompleteAsync(intent.IntentId, timeProvider.GetUtcNow(), ct);
                    completedTotal++;
                }

                logger.LogInformation(
                    "[TaskSchedulingCoordinator] workspace={WorkspaceId} intents={Intents} started={Started} goalTerminal={GoalTerminal} elapsedMs={ElapsedMs}",
                    workspaceId,
                    intents.Count,
                    started,
                    intents.Count(item => item.Source == TaskSchedulerIntentSources.ConversationEvents),
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[TaskSchedulingCoordinator] intent batch failed workspace={WorkspaceId} intents={Intents}",
                    workspaceId,
                    intents.Count);
                foreach (var intent in intents)
                {
                    var dead = await intentStore.FailAsync(
                        intent.IntentId,
                        $"{ex.GetType().Name}: {ex.Message}",
                        Math.Clamp(current.IntentMaxAttempts, 1, 100),
                        timeProvider.GetUtcNow(),
                        ct);
                    if (dead)
                        deadTotal++;
                }
            }
        }

        if (completedTotal > 0 || deadTotal > 0)
        {
            logger.LogInformation(
                "[TaskSchedulingCoordinator] round completed={Completed} dead={Dead} started={Started} elapsedMs={ElapsedMs}",
                completedTotal,
                deadTotal,
                startedTotal,
                sw.ElapsedMilliseconds);
        }
    }
}
