using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Bounded recovery scanner. Shadow mode evaluates only. Authoritative mode
/// dispatches at most one eligible Task per Agent through the single atomic
/// Task -> Goal transaction; it never sends a Task instruction message.
/// </summary>
public sealed class TaskAutoDispatchWorker(
    ITaskAutoDispatchEvaluator evaluator,
    ITaskBacklogRefinementEvaluator backlogRefinementEvaluator,
    ITaskBacklogRefinementStore backlogRefinementStore,
    ITaskExecutionTracker executionTracker,
    ITaskExecutionRepairCoordinator executionRepairCoordinator,
    IExecutionWindowResolver executionWindowResolver,
    ITaskGoalDispatchTransactionStore transactionStore,
    IOptions<TaskAutoDispatchOptions> options,
    IOptions<TaskBoundGoalOptions> taskBoundOptions,
    IOptions<GoalRunOptions> goalOptions,
    TimeProvider timeProvider,
    ILogger<TaskAutoDispatchWorker> logger) : BackgroundService
{
    private readonly TaskAutoDispatchOptions _options = options.Value;
    private readonly TaskBoundGoalOptions _taskBoundOptions = taskBoundOptions.Value;
    private readonly GoalRunOptions _goalOptions = goalOptions.Value;
    private readonly string _ownerId = $"task-goal-coordinator-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("[TaskAutoDispatch] disabled");
            return;
        }

        var authoritative = string.Equals(_options.Mode, "authoritative", StringComparison.OrdinalIgnoreCase);
        if (!authoritative && !string.Equals(_options.Mode, "shadow", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "[TaskAutoDispatch] mode={Mode} refused; supported modes are shadow and authoritative",
                _options.Mode);
            return;
        }
        if (authoritative
            && (!_taskBoundOptions.Enabled || !_goalOptions.Enabled || !_goalOptions.ContinuationEnabled))
        {
            logger.LogCritical(
                "[TaskAutoDispatch] authoritative mode refused; TaskBoundGoals.Enabled, GoalRuns.Enabled and GoalRuns.ContinuationEnabled must all be true");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var workspaceId in (_options.WorkspaceIds ?? [])
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var backlog = await backlogRefinementEvaluator.EvaluateAsync(
                        workspaceId,
                        Math.Clamp(_options.CandidateLimit, 1, 500),
                        stoppingToken);
                    var promoted = authoritative
                        ? await PromoteBacklogAsync(backlog, stoppingToken)
                        : 0;
                    var decisions = await evaluator.EvaluateAsync(
                        workspaceId,
                        Math.Clamp(_options.CandidateLimit, 1, 500),
                        stoppingToken);
                    var started = authoritative
                        ? await DispatchEligibleAsync(decisions, stoppingToken)
                        : 0;
                    var tracking = await executionTracker.EvaluateAsync(
                        workspaceId,
                        Math.Clamp(_options.CandidateLimit, 1, 500),
                        stoppingToken);
                    var repairs = authoritative
                        ? await executionRepairCoordinator.RepairAsync(
                            workspaceId,
                            tracking,
                            stoppingToken)
                        : new TaskExecutionRepairSummary
                        {
                            Examined = tracking.Count,
                            Repaired = 0,
                            RepairedByCode = new Dictionary<string, int>(),
                        };
                    logger.LogInformation(
                        "[TaskAutoDispatch] mode={Mode} workspace={WorkspaceId} backlog={Backlog} refinementReady={RefinementReady} needsRefinement={NeedsRefinement} promoted={Promoted} candidates={Candidates} eligible={Eligible} started={Started} deferred={Deferred} denied={Denied} tracked={Tracked} healthy={Healthy} waiting={Waiting} stalled={Stalled} inconsistent={Inconsistent} cleanupRequired={CleanupRequired} repaired={Repaired} repairCodes={RepairCodes}",
                        authoritative ? "authoritative" : "shadow",
                        workspaceId,
                        backlog.Count,
                        backlog.Count(item => item.Verdict == TaskBacklogRefinementVerdict.ReadyCandidate),
                        backlog.Count(item => item.Verdict == TaskBacklogRefinementVerdict.NeedsRefinement),
                        promoted,
                        decisions.Count,
                        decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible),
                        started,
                        decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Deferred),
                        decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Denied),
                        tracking.Count,
                        tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Healthy),
                        tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Waiting),
                        tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Stalled),
                        tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Inconsistent),
                        tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.CleanupRequired),
                        repairs.Repaired,
                        string.Join(",", repairs.RepairedByCode.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}")));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[TaskAutoDispatch] scan failed workspace={WorkspaceId} mode={Mode}", workspaceId, _options.Mode);
                }
            }

            try
            {
                await Task.Delay(
                    _options.ScanInterval <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(30)
                        : _options.ScanInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<int> PromoteBacklogAsync(
        IReadOnlyList<TaskBacklogRefinementDecision> decisions,
        CancellationToken ct)
    {
        var promoted = 0;
        foreach (var decision in decisions.Where(item =>
                     item.Verdict == TaskBacklogRefinementVerdict.ReadyCandidate))
        {
            if (decision.CompatibleAgentId is null || decision.AgentRoutingFingerprint is null)
                continue;
            var result = await backlogRefinementStore.TryPromoteAsync(new PromoteBacklogTaskCommand
            {
                WorkspaceId = decision.WorkspaceId,
                TaskId = decision.TaskId,
                ExpectedTaskVersion = decision.TaskVersion,
                CompatibleAgentId = decision.CompatibleAgentId,
                ExpectedAgentRoutingFingerprint = decision.AgentRoutingFingerprint,
            }, ct);
            if (result.Promoted)
                promoted++;
            else
                logger.LogInformation(
                    "[TaskAutoDispatch] backlog promotion refused task={TaskId} code={Code}",
                    decision.TaskId,
                    result.Code);
        }
        return promoted;
    }

    private async Task<int> DispatchEligibleAsync(
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        CancellationToken ct)
    {
        var started = 0;
        foreach (var candidate in decisions.Where(item =>
                     item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible))
        {
            if (candidate.AgentId is null
                || candidate.ConversationId is null
                || candidate.AgentRoutingFingerprint is null
                || candidate.ExecutionPlanFingerprint is null
                || candidate.TaskVersion is null
                || candidate.AvailabilityVersion is null
                || candidate.ExecutionWindow is null)
            {
                logger.LogWarning(
                    "[TaskAutoDispatch] incomplete eligible candidate refused task={TaskId} agent={AgentId}",
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
                    "[TaskAutoDispatch] final window fence refused task={TaskId} agent={AgentId} code={Code}",
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
                MinimumIdle = _options.MinimumIdle < TimeSpan.Zero
                    ? TimeSpan.FromMinutes(30)
                    : _options.MinimumIdle,
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
                    "[TaskAutoDispatch] atomic start refused task={TaskId} agent={AgentId} code={Code}",
                    candidate.TaskId,
                    candidate.AgentId,
                    result.Code);
        }
        return started;
    }
}
