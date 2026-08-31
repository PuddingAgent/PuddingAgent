using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Scheduling;

public sealed class TaskAutoDispatchOptions
{
    public const string SectionName = "TaskAutoDispatch";

    /// <summary>Master switch. Default false: no background scans.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Workspace-scoped operator containment. Paused workspaces admit no new
    /// automatic work while status, evaluation and deterministic repair remain available.
    /// </summary>
    public string[] PausedWorkspaceIds { get; set; } = [];

    /// <summary>CAS revision for Admin policy updates.</summary>
    public int PolicyRevision { get; set; }

    /// <summary>
    /// shadow performs evaluation only; authoritative calls the Task-bound Goal
    /// atomic startup store. Both modes default to disabled.
    /// </summary>
    public string Mode { get; set; } = "shadow";

    public string[] WorkspaceIds { get; set; } = ["default"];
    /// <summary>Recovery reconciliation cadence. Events may wake work sooner.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Foreground-idle grace before automatic work can reserve an Agent.</summary>
    public TimeSpan MinimumIdle { get; set; } = TimeSpan.FromMinutes(5);
    public int CandidateLimit { get; set; } = 100;
    /// <summary>
    /// Global admission burst limit per scan. Per-Agent selection remains one,
    /// so this only bounds how many idle Agents may start work together.
    /// </summary>
    public int MaxStartsPerScan { get; set; } = 2;
    /// <summary>
    /// Maximum age without a newer canonical Task/Goal/Iteration/Execution fact
    /// before the read-only tracker reports a stalled execution.
    /// </summary>

    public TimeSpan TrackerStallThreshold { get; set; } = TimeSpan.FromMinutes(30);
    public Dictionary<string, TaskTypeRouteOptions> TaskTypeRoutes { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // ── P0 Scheduler 事件驱动层（event-driven intents）──────────
    /// <summary>Event-driven layer master switch. Default false: the ledger tail bridge stays off and the periodic worker scan remains the only wake-up.</summary>
    public bool EventDrivenEnabled { get; set; }

    /// <summary>Ledger tail poll cadence for new task_events / conversation_events rows.</summary>
    public TimeSpan IntentPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum intents claimed per coordinator round (also the bridge read batch).</summary>
    public int IntentBatchSize { get; set; } = 50;

    /// <summary>Processing lease held by the coordinator while handling claimed intents.</summary>
    public TimeSpan IntentLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Attempts before an intent is parked as dead.</summary>
    public int IntentMaxAttempts { get; set; } = 3;

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
        if (options.MaxStartsPerScan is < 1 or > 32)
            errors.Add("TaskAutoDispatch:MaxStartsPerScan must be between 1 and 32.");
        if (options.TrackerStallThreshold < TimeSpan.FromMinutes(5)
            || options.TrackerStallThreshold > TimeSpan.FromDays(1))
            errors.Add("TaskAutoDispatch:TrackerStallThreshold must be between 5m and 24h.");
        if (options.IntentPollInterval < TimeSpan.FromMilliseconds(250)
            || options.IntentPollInterval > TimeSpan.FromMinutes(5))
            errors.Add("TaskAutoDispatch:IntentPollInterval must be between 250ms and 5m.");
        if (options.IntentBatchSize is < 1 or > 500)
            errors.Add("TaskAutoDispatch:IntentBatchSize must be between 1 and 500.");
        if (options.IntentLease < TimeSpan.FromSeconds(5)
            || options.IntentLease > TimeSpan.FromHours(1))
            errors.Add("TaskAutoDispatch:IntentLease must be between 5s and 1h.");
        if (options.IntentMaxAttempts is < 1 or > 10)
            errors.Add("TaskAutoDispatch:IntentMaxAttempts must be between 1 and 10.");

        foreach (var (taskType, route) in options.TaskTypeRoutes)
        {
            if (string.IsNullOrWhiteSpace(taskType) || taskType.Length > 64)
                errors.Add("TaskAutoDispatch:TaskTypeRoutes keys must be 1-64 characters.");
            if (route.RequiredCapabilityIds.Any(string.IsNullOrWhiteSpace))
                errors.Add($"TaskAutoDispatch:TaskTypeRoutes:{taskType} contains an empty capability.");
            if (route.AllowedRoles.Any(string.IsNullOrWhiteSpace))
                errors.Add($"TaskAutoDispatch:TaskTypeRoutes:{taskType} contains an empty role.");
        }
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

public sealed class TaskTypeRouteOptions
{
    public string[] RequiredCapabilityIds { get; set; } = [];
    public string[] AllowedRoles { get; set; } = [];
    public string? RequiredProviderId { get; set; }
    public string? RequiredModelId { get; set; }
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
    IWorkspaceAgentCatalog agentCatalog,
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
                  AND auto_dispatch_enabled = 1
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

        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var availabilityByAgent = new Dictionary<string, AgentAvailabilitySnapshot>(
            StringComparer.Ordinal);
        foreach (var agent in agents.OrderBy(item => item.AgentId, StringComparer.Ordinal))
        {
            availabilityByAgent[agent.AgentId] = await availabilityStore.RebuildAsync(
                workspaceId,
                agent.AgentId,
                ct);
        }
        var decisions = new List<TaskAutoDispatchCandidateDecision>(candidates.Count);
        var selectedAgents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var timeGate = Later(task.NotBeforeUtc, task.NextEligibleAtUtc);
            if (timeGate > now)
            {
                decisions.Add(Decision(workspaceId, task.TaskId, task.PreferredAgentId,
                    TaskAutoDispatchCandidateVerdict.Deferred, "task_not_yet_eligible", now,
                    taskType: task.TaskType, nextEligibleAtUtc: timeGate));
                continue;
            }

            var dependency = await dependencyStore.EvaluateAsync(workspaceId, task.TaskId, ct);
            if (dependency.State != TaskDependencyEvaluationState.Satisfied)
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    task.PreferredAgentId,
                    dependency.State == TaskDependencyEvaluationState.Broken
                        ? TaskAutoDispatchCandidateVerdict.Denied
                        : TaskAutoDispatchCandidateVerdict.Deferred,
                    dependency.State == TaskDependencyEvaluationState.Broken
                        ? "task_dependency_broken"
                        : "task_dependency_waiting",
                    now,
                    taskType: task.TaskType,
                    dependencyState: dependency.State.ToString().ToLowerInvariant()));
                continue;
            }

            _options.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);
            if (!TaskExecutionPlanCompiler.TryCompile(task, typeRoute, out var executionPlan, out var planCode))
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    task.PreferredAgentId,
                    TaskAutoDispatchCandidateVerdict.Denied,
                    planCode,
                    now,
                    taskType: task.TaskType,
                    taskVersion: task.Version,
                    dependencyState: "satisfied"));
                continue;
            }
            var routedAgents = agents
                .Select(agent => (Agent: agent, Route: TaskAgentRouteMatcher.Evaluate(task, agent, typeRoute)))
                .Where(item => item.Route.Compatible)
                .OrderByDescending(item => string.Equals(
                    task.PreferredAgentId, item.Agent.AgentId, StringComparison.Ordinal))
                .ThenBy(item => item.Agent.AgentId, StringComparer.Ordinal)
                .ToArray();
            if (routedAgents.Length == 0)
            {
                decisions.Add(Decision(
                    workspaceId,
                    task.TaskId,
                    task.PreferredAgentId,
                    TaskAutoDispatchCandidateVerdict.Denied,
                    !task.AllowAgentFallback && !string.IsNullOrWhiteSpace(task.PreferredAgentId)
                        ? "preferred_agent_unavailable_or_incompatible"
                        : "no_compatible_agent",
                    now,
                    taskType: task.TaskType,
                    dependencyState: "satisfied"));
                continue;
            }

            TaskAutoDispatchCandidateDecision? lastDeferred = null;
            foreach (var routed in routedAgents)
            {
                var agentId = routed.Agent.AgentId;
                var availability = availabilityByAgent[agentId];
                if (!availability.CanAcceptAutomaticTask(now))
                {
                    lastDeferred = Decision(workspaceId, task.TaskId, agentId,
                        TaskAutoDispatchCandidateVerdict.Deferred, "agent_not_idle", now,
                        taskType: task.TaskType,
                        agentSelectionCode: routed.Route.Code,
                        routingFingerprint: routed.Route.Fingerprint,
                        availabilityVersion: availability.Version,
                        availabilityReason: availability.ReasonCode,
                        dependencyState: "satisfied");
                    continue;
                }

                var minimumIdle = _options.MinimumIdle < TimeSpan.Zero
                    ? TimeSpan.FromMinutes(30)
                    : _options.MinimumIdle;
                var minimumIdleUntil = availability.IdleSinceUtc?.Add(minimumIdle);
                if (minimumIdleUntil > now)
                {
                    lastDeferred = Decision(workspaceId, task.TaskId, agentId,
                        TaskAutoDispatchCandidateVerdict.Deferred, "agent_idle_grace_period", now,
                        taskType: task.TaskType,
                        agentSelectionCode: routed.Route.Code,
                        routingFingerprint: routed.Route.Fingerprint,
                        nextEligibleAtUtc: minimumIdleUntil,
                        availabilityVersion: availability.Version,
                        availabilityReason: availability.ReasonCode,
                        dependencyState: "satisfied");
                    continue;
                }

                var window = await executionWindowResolver.EvaluateAsync(
                    workspaceId, agentId, task.ExecutionWindow, now, ct);
                if (window.Verdict != ExecutionWindowVerdict.Allow)
                {
                    lastDeferred = Decision(workspaceId, task.TaskId, agentId,
                        TaskAutoDispatchCandidateVerdict.Deferred,
                        window.Verdict == ExecutionWindowVerdict.Unknown
                            ? "execution_window_unknown"
                            : "execution_window_closed",
                        now,
                        taskType: task.TaskType,
                        agentSelectionCode: routed.Route.Code,
                        routingFingerprint: routed.Route.Fingerprint,
                        nextEligibleAtUtc: window.NextEligibleAtUtc,
                        availabilityVersion: availability.Version,
                        availabilityReason: availability.ReasonCode,
                        dependencyState: "satisfied",
                        windowCode: window.Code);
                    continue;
                }

                if (!selectedAgents.Add(agentId))
                {
                    lastDeferred = Decision(workspaceId, task.TaskId, agentId,
                        TaskAutoDispatchCandidateVerdict.Deferred,
                        "agent_already_selected_this_scan", now,
                        taskType: task.TaskType,
                        agentSelectionCode: routed.Route.Code,
                        routingFingerprint: routed.Route.Fingerprint,
                        availabilityVersion: availability.Version,
                        availabilityReason: availability.ReasonCode,
                        dependencyState: "satisfied",
                        windowCode: window.Code);
                    continue;
                }

                decisions.Add(Decision(workspaceId, task.TaskId, agentId,
                    TaskAutoDispatchCandidateVerdict.Eligible, "eligible", now,
                    taskType: task.TaskType,
                    agentSelectionCode: routed.Route.Code,
                    routingFingerprint: routed.Route.Fingerprint,
                    taskVersion: task.Version,
                    conversationId: availability.MainConversationId,
                    executionWindow: task.ExecutionWindow,
                    executionPlanFingerprint: executionPlan!.Fingerprint,
                    executionPlanSchemaVersion: executionPlan.SchemaVersion,
                    executionPlanVersion: executionPlan.PlanVersion,
                    availabilityVersion: availability.Version,
                    availabilityReason: availability.ReasonCode,
                    dependencyState: "satisfied",
                    windowCode: window.Code));
                lastDeferred = null;
                break;
            }

            if (lastDeferred is not null)
                decisions.Add(lastDeferred);
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
        string? taskType = null,
        string? agentSelectionCode = null,
        string? routingFingerprint = null,
        DateTimeOffset? nextEligibleAtUtc = null,
        int? taskVersion = null,
        string? conversationId = null,
        TaskExecutionWindow? executionWindow = null,
        string? executionPlanFingerprint = null,
        int? executionPlanSchemaVersion = null,
        int? executionPlanVersion = null,
        long? availabilityVersion = null,
        string? availabilityReason = null,
        string? dependencyState = null,
        string? windowCode = null) => new()
    {
        WorkspaceId = workspaceId,
        TaskId = taskId,
        TaskVersion = taskVersion,
        AgentId = agentId,
        TaskType = taskType,
        AgentSelectionCode = agentSelectionCode,
        AgentRoutingFingerprint = routingFingerprint,
        ConversationId = conversationId,
        ExecutionWindow = executionWindow,
        ExecutionPlanFingerprint = executionPlanFingerprint,
        ExecutionPlanSchemaVersion = executionPlanSchemaVersion,
        ExecutionPlanVersion = executionPlanVersion,
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
