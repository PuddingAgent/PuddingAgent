using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>事件驱动协调器使用的派发启动边界（与 Worker 的 DispatchEligibleAsync 同语义）。</summary>
public interface ITaskAutoDispatchStarter
{
    /// <summary>对 Eligible 决策执行围栏校验 + 二次 window fence + 原子启动，返回启动数。</summary>
    Task<int> DispatchAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        CancellationToken ct = default);
}

/// <summary>
/// Task → Goal 派发启动器（独立于 <see cref="TaskAutoDispatchWorker"/> 的启动路径）。
/// <para>
/// TODO-unify: DispatchAsync 刻意复制 Worker.DispatchEligibleAsync 的同等语义（围栏字段校验 +
/// 二次 window fence + TaskGoalDispatchTransactionStore.StartAsync + LostRace 容忍），而非重构
/// Worker——工作区存在他方在途改动，禁止触碰该文件；后续由统一收编批次合并两条启动路径。
/// </para>
/// </summary>
public sealed class TaskAutoDispatchStarter(
    IExecutionWindowResolver executionWindowResolver,
    ITaskGoalDispatchTransactionStore transactionStore,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    IOptions<TaskBoundGoalOptions> taskBoundOptions,
    TimeProvider timeProvider,
    ILogger<TaskAutoDispatchStarter> logger) : ITaskAutoDispatchStarter
{
    private readonly TaskBoundGoalOptions _taskBoundOptions = taskBoundOptions.Value;
    private readonly string _ownerId = $"task-goal-starter-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public async Task<int> DispatchAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        CancellationToken ct = default)
    {
        var current = options.CurrentValue;
        var started = 0;
        foreach (var candidate in decisions.Where(item =>
                     item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible))
        {
            if (started >= Math.Clamp(current.MaxStartsPerScan, 1, 32))
                break;
            if (candidate.AgentId is null
                || candidate.ConversationId is null
                || candidate.AgentRoutingFingerprint is null
                || candidate.ExecutionPlanFingerprint is null
                || candidate.TaskVersion is null
                || candidate.AvailabilityVersion is null
                || candidate.ExecutionWindow is null)
            {
                logger.LogWarning(
                    "[TaskAutoDispatchStarter] incomplete eligible candidate refused task={TaskId} agent={AgentId}",
                    candidate.TaskId,
                    candidate.AgentId);
                continue;
            }

            // Second fence: recompute the window immediately before the atomic
            // transaction. The store then checks this immutable decision's TTL.
            var now = timeProvider.GetUtcNow();
            var window = await executionWindowResolver.EvaluateAsync(
                candidate.WorkspaceId,
                candidate.AgentId,
                candidate.ExecutionWindow.Value,
                now,
                ct);
            if (window.Verdict != ExecutionWindowVerdict.Allow)
            {
                logger.LogInformation(
                    "[TaskAutoDispatchStarter] final window fence refused task={TaskId} agent={AgentId} code={Code}",
                    candidate.TaskId,
                    candidate.AgentId,
                    window.Code);
                continue;
            }

            var result = await transactionStore.StartAsync(new StartGoalFromTaskCommand
            {
                WorkspaceId = candidate.WorkspaceId,
                TaskId = candidate.TaskId,
                ExpectedTaskVersion = candidate.TaskVersion.Value,
                AgentId = candidate.AgentId,
                ExpectedAgentRoutingFingerprint = candidate.AgentRoutingFingerprint,
                ExpectedExecutionPlanFingerprint = candidate.ExecutionPlanFingerprint,
                ConversationId = candidate.ConversationId,
                ExpectedAvailabilityVersion = candidate.AvailabilityVersion.Value,
                ExecutionWindow = candidate.ExecutionWindow.Value,
                WindowDecision = window,
                GoalIterationBudget = _taskBoundOptions.GoalIterationBudget,
                MinimumIdle = current.MinimumIdle < TimeSpan.Zero
                    ? TimeSpan.FromMinutes(30)
                    : current.MinimumIdle,
                ReservationLease = _taskBoundOptions.ReservationLease,
                RequestedAtUtc = now,
                OwnerId = _ownerId,
                CausationId = "task-auto-dispatch",
                CorrelationId = candidate.TaskId,
                IdempotencyKey = $"task-goal:{candidate.WorkspaceId}:{candidate.TaskId}:{candidate.TaskVersion.Value}",
            }, ct);
            if (result.Started && result.Code == TaskBoundGoalStartCodes.Started)
                started++;
            else if (!result.Started)
                logger.LogInformation(
                    "[TaskAutoDispatchStarter] atomic start refused task={TaskId} agent={AgentId} code={Code}",
                    candidate.TaskId,
                    candidate.AgentId,
                    result.Code);
        }

        return started;
    }
}
