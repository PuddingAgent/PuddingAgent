using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 事件驱动协调器：消费 task_scheduler_intents → task-scoped 即时候选评估 → 持久化
/// decision/outcome →（authoritative）派发启动 → 逐 Intent 结算。
/// <para>
/// 结算合同（卡 3bd2a4b0 / 实施方案 §3.2、§5）：每个领取的 Intent 必须先落 durable
/// outcome（task_scheduler_intent_outcomes，PK intent_id 幂等）才能 done；
/// candidate decision 在派发「之前」落库；决策落库失败即 fail closed——不启动、不结算，
/// Intent 走 FailAsync 保留租约重试。crash 后重放依赖 Intent 主键幂等与 starter fence。
/// 仅在 TaskAutoDispatch.Enabled 且 Mode=authoritative 时启动（mode 归一化收编属下一批次）。
/// </para>
/// </summary>
public sealed class TaskSchedulingCoordinator(
    ITaskSchedulerIntentStore intentStore,
    ITaskAutoDispatchEvaluator evaluator,
    ITaskAutoDispatchStarter starter,
    IWorkspaceAgentCatalog agentCatalog,
    IAgentAvailabilityProjectionStore availabilityStore,
    TaskSchedulerDecisionStore decisionStore,
    ITaskSchedulerIntentOutcomeStore outcomeStore,
    IDbContextFactory<PlatformDbContext> dbFactory,
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
                startedTotal += await ProcessBatchAsync(workspaceId, intents, current, ct);
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

    /// <summary>
    /// 单批结算：去重 TaskId → 分类可评估性 → task-scoped 评估 → decision 持久化（fail closed）
    /// → 逐卡派发 → 全部 Intent 的 outcome 落库 → 逐 Intent done。任何一步抛出都让本批
    /// intent 保留租约重试（外层 FailAsync），绝不出现「无 outcome 的 done」。
    /// </summary>
    private async Task<int> ProcessBatchAsync(
        string workspaceId,
        IReadOnlyList<TaskSchedulerIntent> intents,
        TaskAutoDispatchOptions current,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var batchId = Guid.NewGuid().ToString("N");
        var scanId = $"event-{workspaceId}-{batchId}";
        var normalizedMode = TaskAutoDispatchOptions.NormalizeMode(current.Mode);

        // goal 终态事件改变 Agent 可用性事实：先从已提交事实重建投影再评估（§5.3 步骤 1）。
        if (intents.Any(item => item.Source == TaskSchedulerIntentSources.ConversationEvents))
        {
            var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
            foreach (var agent in agents.OrderBy(item => item.AgentId, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                await availabilityStore.RebuildAsync(workspaceId, agent.AgentId, ct);
            }
        }

        // 按 TaskId 去重；无 TaskId 的（goal 终态）intent 只触发上面的可用性刷新 → noop。
        var taskGroups = intents
            .Where(item => !string.IsNullOrWhiteSpace(item.TaskId))
            .GroupBy(item => item.TaskId!, StringComparer.Ordinal)
            .ToList();
        var noopIntents = intents.Where(item => string.IsNullOrWhiteSpace(item.TaskId)).ToList();

        // 读取触发 Task 的当前事实：终态/未 opt-in/不可评估状态直接落 outcome，不进评估（步骤 2）。
        var taskIds = taskGroups.Select(group => group.Key).ToList();
        var preOutcomeByTask = new Dictionary<string, (string Outcome, string Reason)>(StringComparer.Ordinal);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var tasks = await db.WorkspaceTasks
                .AsNoTracking()
                .Where(entity => entity.WorkspaceId == workspaceId && taskIds.Contains(entity.TaskId))
                .ToListAsync(ct);
            var tasksById = tasks.ToDictionary(entity => entity.TaskId, StringComparer.Ordinal);
            foreach (var group in taskGroups)
            {
                if (!tasksById.TryGetValue(group.Key, out var task))
                {
                    preOutcomeByTask[group.Key] = (TaskSchedulerIntentOutcomes.Ineligible, "task_missing");
                    continue;
                }

                var statusLower = task.Status.ToString().ToLowerInvariant();
                preOutcomeByTask[group.Key] = task.Status switch
                {
                    WorkspaceTaskStatus.Completed
                        or WorkspaceTaskStatus.Failed
                        or WorkspaceTaskStatus.Cancelled
                        or WorkspaceTaskStatus.Archived
                        => (TaskSchedulerIntentOutcomes.Terminal, statusLower),
                    _ when !task.AutoDispatchEnabled
                        => (TaskSchedulerIntentOutcomes.Ineligible, "not_opted_in"),
                    _ when task.Status is not (WorkspaceTaskStatus.Ready or WorkspaceTaskStatus.Deferred)
                        => (TaskSchedulerIntentOutcomes.Ineligible, $"status_{statusLower}"),
                    _ => (string.Empty, string.Empty),
                };
            }
        }

        var evaluableIds = taskGroups
            .Where(group => !preOutcomeByTask.TryGetValue(group.Key, out var classified)
                            || classified.Outcome.Length == 0)
            .Select(group => group.Key)
            .ToList();

        // task-scoped 评估：候选仍限于 Ready/Deferred + opt-in（§5.2 合同）。
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions = evaluableIds.Count == 0
            ? []
            : await evaluator.EvaluateTasksAsync(
                workspaceId,
                evaluableIds,
                Math.Clamp(current.CandidateLimit, 1, 500),
                ct);

        // fail closed（§5.4）：candidate decision 必须先于派发 durable；写失败直接抛出，
        // 由外层把本批 intent FailAsync——不启动、不结算、不吞错后 done。
        if (decisions.Count > 0)
        {
            await decisionStore.RecordCandidateDecisionsAsync(workspaceId, normalizedMode, scanId, decisions, ct);
        }

        var decisionIdByTask = decisions.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await decisionStore.GetCandidateDecisionIdsAsync(scanId, ct);

        // 逐卡启动结果（步骤 6）：上限统一走 staged mode 生效值；cap 外的 Eligible 卡无
        // 启动结果，outcome 回退为 deferred(decision code)。
        var startOutcomes = decisions.Count == 0
            ? Array.Empty<TaskAutoDispatchStartOutcome>()
            : await starter.DispatchDetailedAsync(
                decisions,
                TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(current),
                ct);
        var startByTask = startOutcomes
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        // 组装每个 Intent 的 outcome（步骤 7：先写 outcome，全部成功后才 Complete）。
        var decisionByTask = decisions
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var records = new List<TaskSchedulerIntentOutcomeRecord>(intents.Count);
        foreach (var intent in noopIntents)
        {
            records.Add(BuildRecord(intent, TaskSchedulerIntentOutcomes.Noop, scanId, current, now,
                reasonCode: "availability_refreshed"));
        }

        foreach (var group in taskGroups)
        {
            string? outcome = null;
            string? reason = null;
            if (preOutcomeByTask.TryGetValue(group.Key, out var classified)
                && classified.Outcome.Length > 0)
            {
                outcome = classified.Outcome;
                reason = classified.Reason;
            }

            string? decisionId = null;
            string? assignmentId = null;
            string? goalRunId = null;
            if (outcome is null)
            {
                if (!decisionByTask.TryGetValue(group.Key, out var decision))
                {
                    (outcome, reason) = (TaskSchedulerIntentOutcomes.Ineligible, "not_evaluated");
                }
                else
                {
                    decisionId = decisionIdByTask.GetValueOrDefault(group.Key);
                    if (decision.Verdict == TaskAutoDispatchCandidateVerdict.Eligible
                        && startByTask.TryGetValue(group.Key, out var start)
                        && start.Started)
                    {
                        outcome = TaskSchedulerIntentOutcomes.Started;
                        reason = start.Code;
                        assignmentId = start.AssignmentId;
                        goalRunId = start.GoalRunId;
                    }
                    else if (decision.Verdict == TaskAutoDispatchCandidateVerdict.Denied)
                    {
                        outcome = TaskSchedulerIntentOutcomes.Denied;
                        reason = decision.Code;
                    }
                    else
                    {
                        outcome = TaskSchedulerIntentOutcomes.Deferred;
                        reason = decision.Code;
                    }
                }
            }

            foreach (var intent in group)
            {
                records.Add(BuildRecord(intent, outcome!, scanId, current, now,
                    taskId: group.Key,
                    reasonCode: reason,
                    decisionId: decisionId,
                    assignmentId: assignmentId,
                    goalRunId: goalRunId));
            }
        }

        await outcomeStore.RecordAsync(records, ct);
        foreach (var intent in intents)
        {
            await intentStore.CompleteAsync(intent.IntentId, timeProvider.GetUtcNow(), ct);
        }

        logger.LogInformation(
            "[TaskSchedulingCoordinator] workspace={WorkspaceId} intents={Intents} tasks={Tasks} decisions={Decisions} started={Started} outcomes={Outcomes} goalTerminal={GoalTerminal} scanId={ScanId}",
            workspaceId,
            intents.Count,
            taskGroups.Count,
            decisions.Count,
            startOutcomes.Count(item => item.Started),
            records.Count,
            noopIntents.Count,
            scanId);
        return startOutcomes.Count(item => item.Started);
    }

    private static TaskSchedulerIntentOutcomeRecord BuildRecord(
        TaskSchedulerIntent intent,
        string outcome,
        string scanId,
        TaskAutoDispatchOptions current,
        DateTimeOffset now,
        string? taskId = null,
        string? reasonCode = null,
        string? decisionId = null,
        string? assignmentId = null,
        string? goalRunId = null) => new()
    {
        IntentId = intent.IntentId,
        WorkspaceId = intent.WorkspaceId,
        TaskId = taskId ?? intent.TaskId,
        Outcome = outcome,
        DecisionId = decisionId,
        ScanId = scanId,
        PolicyRevision = current.PolicyRevision,
        OptionsHash = ComputeOptionsHash(current),
        ReasonCode = reasonCode,
        StartedAssignmentId = assignmentId,
        StartedGoalRunId = goalRunId,
        CreatedAtUtc = now,
    };

    /// <summary>触发本轮结算的选项指纹（mode/cap/窗口等调度语义输入；不含机密）。</summary>
    private static string ComputeOptionsHash(TaskAutoDispatchOptions current)
    {
        var payload = string.Join('|',
            "v1",
            TaskAutoDispatchOptions.NormalizeMode(current.Mode),
            current.CandidateLimit,
            TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(current),
            (int)current.MinimumIdle.TotalSeconds,
            (int)current.ScanInterval.TotalSeconds,
            current.EventDrivenEnabled,
            current.PolicyRevision);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
