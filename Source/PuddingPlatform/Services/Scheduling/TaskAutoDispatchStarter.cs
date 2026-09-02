using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>单卡启动结果（事件驱动 Coordinator 用于逐 Intent 落 outcome，§5.3 步骤 6）。</summary>
public sealed record TaskAutoDispatchStartOutcome
{
    public required string TaskId { get; init; }
    public required bool Started { get; init; }
    public required string Code { get; init; }
    public string? AgentId { get; init; }
    public string? AssignmentId { get; init; }
    public string? GoalRunId { get; init; }
}

/// <summary>事件驱动协调器使用的派发启动边界（与 Worker 的 DispatchEligibleAsync 同语义）。</summary>
public interface ITaskAutoDispatchStarter
{
    /// <summary>对 Eligible 决策执行围栏校验 + 二次 window fence + 原子启动，返回启动数。
    /// maxStartsOverride 供 staged 灰度（authoritative-single 强制 1）覆盖配置值。</summary>
    Task<int> DispatchAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        int? maxStartsOverride = null,
        CancellationToken ct = default);

    /// <summary>同 DispatchAsync，但返回逐 Task 启动结果（含拒绝码与 Assignment/Goal id）。</summary>
    Task<IReadOnlyList<TaskAutoDispatchStartOutcome>> DispatchDetailedAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        int? maxStartsOverride = null,
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
        int? maxStartsOverride = null,
        CancellationToken ct = default)
    {
        var outcomes = await DispatchDetailedAsync(decisions, maxStartsOverride, ct);
        return outcomes.Count(item => item.Started);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskAutoDispatchStartOutcome>> DispatchDetailedAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        int? maxStartsOverride = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<TaskAutoDispatchStartOutcome>();
        var current = options.CurrentValue;
        var maxStarts = Math.Clamp(maxStartsOverride ?? current.MaxStartsPerScan, 1, 32);
        foreach (var candidate in decisions.Where(item =>
                     item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible))
        {
            if (outcomes.Count(item => item.Started) >= maxStarts)
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
                outcomes.Add(new TaskAutoDispatchStartOutcome
                {
                    TaskId = candidate.TaskId,
                    Started = false,
                    Code = "incomplete_candidate",
                    AgentId = candidate.AgentId,
                });
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
                outcomes.Add(new TaskAutoDispatchStartOutcome
                {
                    TaskId = candidate.TaskId,
                    Started = false,
                    Code = string.IsNullOrWhiteSpace(window.Code) ? "window_refused" : window.Code,
                    AgentId = candidate.AgentId,
                });
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
            outcomes.Add(new TaskAutoDispatchStartOutcome
            {
                TaskId = candidate.TaskId,
                Started = result.Started,
                Code = result.Code,
                AgentId = candidate.AgentId,
                AssignmentId = result.AssignmentId,
                GoalRunId = result.GoalRunId,
            });
            if (!result.Started)
                logger.LogInformation(
                    "[TaskAutoDispatchStarter] atomic start refused task={TaskId} agent={AgentId} code={Code}",
                    candidate.TaskId,
                    candidate.AgentId,
                    result.Code);
        }

        return outcomes;
    }
}
