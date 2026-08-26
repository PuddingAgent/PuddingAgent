using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

public sealed class TaskAutoDispatchOptions
{
    public const string SectionName = "TaskAutoDispatch";

    /// <summary>Master switch. Default false: no background scans.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// shadow performs evaluation only; authoritative calls the Task-bound Goal
    /// atomic startup store. Both modes default to disabled.
    /// </summary>
    public string Mode { get; set; } = "shadow";

    public string[] WorkspaceIds { get; set; } = ["default"];
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinimumIdle { get; set; } = TimeSpan.FromMinutes(30);
    public int CandidateLimit { get; set; } = 100;

    public static IReadOnlyList<string> Validate(
        TaskAutoDispatchOptions options,
        TaskBoundGoalOptions taskBoundGoals,
        GoalRunOptions goalRuns)
    {
        var errors = new List<string>();
        if (!string.Equals(options.Mode, "shadow", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Mode, "authoritative", StringComparison.OrdinalIgnoreCase))
            errors.Add("TaskAutoDispatch:Mode must be shadow or authoritative.");
        if (options.ScanInterval < TimeSpan.FromSeconds(1)
            || options.ScanInterval > TimeSpan.FromHours(1))
            errors.Add("TaskAutoDispatch:ScanInterval must be between 1s and 1h.");
        if (options.MinimumIdle < TimeSpan.Zero || options.MinimumIdle > TimeSpan.FromDays(1))
            errors.Add("TaskAutoDispatch:MinimumIdle must be between 0 and 24h.");
        if (options.CandidateLimit is < 1 or > 500)
            errors.Add("TaskAutoDispatch:CandidateLimit must be between 1 and 500.");
        if (options.Enabled
            && string.Equals(options.Mode, "authoritative", StringComparison.OrdinalIgnoreCase)
            && (!taskBoundGoals.Enabled || !goalRuns.Enabled || !goalRuns.ContinuationEnabled))
        {
            errors.Add(
                "Authoritative TaskAutoDispatch requires TaskBoundGoals:Enabled, " +
                "GoalRuns:Enabled and GoalRuns:ContinuationEnabled.");
        }
        return errors;
    }
}

/// <summary>
/// Deterministic, side-effect-free scheduler planner. It proves dependency,
/// logical availability and execution-window gates and selects at most one
/// Task per Agent. It does not reserve, mutate, send or create a Goal.
/// </summary>
public sealed class TaskAutoDispatchEvaluator(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ITaskDependencyStore dependencyStore,
    IAgentAvailabilityProjectionStore availabilityStore,
    IExecutionWindowResolver executionWindowResolver,
    IOptions<TaskAutoDispatchOptions> options,
    TimeProvider timeProvider) : ITaskAutoDispatchEvaluator
{
    private readonly TaskAutoDispatchOptions _options = options.Value;

    public async Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // EF SQLite cannot translate DateTimeOffset ORDER BY. The columns are
        // persisted as ISO timestamps, so SQLite julianday gives deterministic
        // UTC ordering while the LIMIT keeps every scan bounded.
        var candidates = await db.WorkspaceTasks
            .FromSqlInterpolated($"""
                SELECT *
                FROM workspace_tasks
                WHERE workspace_id = {workspaceId}
                  AND status IN ({(int)WorkspaceTaskStatus.Ready}, {(int)WorkspaceTaskStatus.Deferred})
                ORDER BY priority ASC,
                         CASE WHEN due_at_utc IS NULL THEN 1 ELSE 0 END ASC,
                         julianday(due_at_utc) ASC,
                         CASE WHEN not_before_utc IS NULL THEN 1 ELSE 0 END ASC,
                         julianday(not_before_utc) ASC,
                         julianday(created_at_utc) ASC,
                         sort_order ASC,
                         task_id ASC
                LIMIT {limit}
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        var decisions = new List<TaskAutoDispatchCandidateDecision>(candidates.Count);
        var selectedAgents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(task.PreferredAgentId))
            {
                decisions.Add(Decision(workspaceId, task.TaskId, null, TaskAutoDispatchCandidateVerdict.Denied,
                    "preferred_agent_required", now));
                continue;
            }

            var agentId = task.PreferredAgentId;
            var timeGate = Later(task.NotBeforeUtc, task.NextEligibleAtUtc);
            if (timeGate > now)
            {
                decisions.Add(Decision(workspaceId, task.TaskId, agentId, TaskAutoDispatchCandidateVerdict.Deferred,
                    "task_not_yet_eligible", now, nextEligibleAtUtc: timeGate));
                continue;
            }

            var dependency = await dependencyStore.EvaluateAsync(workspaceId, task.TaskId, ct);
            if (dependency.State != TaskDependencyEvaluationState.Satisfied)
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    agentId,
                    dependency.State == TaskDependencyEvaluationState.Broken
                        ? TaskAutoDispatchCandidateVerdict.Denied
                        : TaskAutoDispatchCandidateVerdict.Deferred,
                    dependency.State == TaskDependencyEvaluationState.Broken
                        ? "task_dependency_broken"
                        : "task_dependency_waiting",
                    now,
                    dependencyState: dependency.State.ToString().ToLowerInvariant()));
                continue;
            }

            var availability = await availabilityStore.RebuildAsync(workspaceId, agentId, ct);
            if (!availability.CanAcceptAutomaticTask(now))
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    agentId,
                    TaskAutoDispatchCandidateVerdict.Deferred,
                    "agent_not_idle",
                    now,
                    availabilityVersion: availability.Version,
                    availabilityReason: availability.ReasonCode,
                    dependencyState: "satisfied"));
                continue;
            }

            var minimumIdle = _options.MinimumIdle < TimeSpan.Zero
                ? TimeSpan.FromMinutes(30)
                : _options.MinimumIdle;
            var minimumIdleUntil = availability.IdleSinceUtc?.Add(minimumIdle);
            if (minimumIdleUntil > now)
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    agentId,
                    TaskAutoDispatchCandidateVerdict.Deferred,
                    "agent_idle_grace_period",
                    now,
                    nextEligibleAtUtc: minimumIdleUntil,
                    availabilityVersion: availability.Version,
                    availabilityReason: availability.ReasonCode,
                    dependencyState: "satisfied"));
                continue;
            }

            var window = await executionWindowResolver.EvaluateAsync(
                workspaceId,
                agentId,
                task.ExecutionWindow,
                now,
                ct);
            if (window.Verdict != ExecutionWindowVerdict.Allow)
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    agentId,
                    TaskAutoDispatchCandidateVerdict.Deferred,
                    window.Verdict == ExecutionWindowVerdict.Unknown
                        ? "execution_window_unknown"
                        : "execution_window_closed",
                    now,
                    nextEligibleAtUtc: window.NextEligibleAtUtc,
                    availabilityVersion: availability.Version,
                    availabilityReason: availability.ReasonCode,
                    dependencyState: "satisfied",
                    windowCode: window.Code));
                continue;
            }

            if (!selectedAgents.Add(agentId))
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    agentId,
                    TaskAutoDispatchCandidateVerdict.Deferred,
                    "agent_already_selected_this_scan",
                    now,
                    availabilityVersion: availability.Version,
                    availabilityReason: availability.ReasonCode,
                    dependencyState: "satisfied",
                    windowCode: window.Code));
                continue;
            }

            decisions.Add(Decision(
                workspaceId,
                task.TaskId,
                agentId,
                TaskAutoDispatchCandidateVerdict.Eligible,
                "eligible",
                now,
                taskVersion: task.Version,
                conversationId: availability.MainConversationId,
                executionWindow: task.ExecutionWindow,
                availabilityVersion: availability.Version,
                availabilityReason: availability.ReasonCode,
                dependencyState: "satisfied",
                windowCode: window.Code));
        }

        return decisions;
    }

    private static TaskAutoDispatchCandidateDecision Decision(
        string workspaceId,
        string taskId,
        string? agentId,
        TaskAutoDispatchCandidateVerdict verdict,
        string code,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleAtUtc = null,
        int? taskVersion = null,
        string? conversationId = null,
        TaskExecutionWindow? executionWindow = null,
        long? availabilityVersion = null,
        string? availabilityReason = null,
        string? dependencyState = null,
        string? windowCode = null) => new()
    {
        WorkspaceId = workspaceId,
        TaskId = taskId,
        TaskVersion = taskVersion,
        AgentId = agentId,
        ConversationId = conversationId,
        ExecutionWindow = executionWindow,
        Verdict = verdict,
        Code = code,
        EvaluatedAtUtc = now,
        NextEligibleAtUtc = nextEligibleAtUtc,
        AvailabilityVersion = availabilityVersion,
        AvailabilityReason = availabilityReason,
        DependencyState = dependencyState,
        WindowCode = windowCode,
    };

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (!first.HasValue)
            return second;
        if (!second.HasValue)
            return first;
        return first > second ? first : second;
    }
}
