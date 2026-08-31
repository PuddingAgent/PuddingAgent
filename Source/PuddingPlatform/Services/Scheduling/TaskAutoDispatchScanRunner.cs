using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PuddingCode.Scheduling;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// One bounded scheduler reconciliation round shared by the periodic worker and
/// the Admin control plane. Keeping one runner prevents "scan now" from becoming
/// a second scheduler with different ordering or fences.
/// </summary>
public sealed class TaskAutoDispatchScanRunner(
    ITaskAutoDispatchEvaluator evaluator,
    ITaskBacklogRefinementEvaluator backlogRefinementEvaluator,
    ITaskBacklogRefinementStore backlogRefinementStore,
    ITaskExecutionTracker executionTracker,
    ITaskExecutionRepairCoordinator executionRepairCoordinator,
    IWorkspaceAgentCatalog agentCatalog,
    IAgentAvailabilityProjectionStore availabilityStore,
    ITaskAutoDispatchStarter starter,
    TimeProvider timeProvider,
    ILogger<TaskAutoDispatchScanRunner> logger)
{
    public async Task<TaskAutoDispatchScanSummary> RunAsync(
        string workspaceId,
        string mode,
        int candidateLimit,
        string trigger,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var authoritative = string.Equals(mode, "authoritative", StringComparison.OrdinalIgnoreCase);
        if (!authoritative && !string.Equals(mode, "shadow", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("scheduler_mode_invalid");

        var startedAt = timeProvider.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var limit = Math.Clamp(candidateLimit, 1, 500);

        // Ownership reconciliation must run before availability and selection.
        var tracking = await executionTracker.EvaluateAsync(workspaceId, limit, ct);
        var repairs = authoritative
            ? await executionRepairCoordinator.RepairAsync(workspaceId, tracking, ct)
            : EmptyRepair(tracking.Count);
        var availability = await RefreshAvailabilityAsync(workspaceId, ct);
        var backlog = await backlogRefinementEvaluator.EvaluateAsync(workspaceId, limit, ct);
        var promoted = authoritative ? await PromoteBacklogAsync(backlog, ct) : 0;
        var decisions = await evaluator.EvaluateAsync(workspaceId, limit, ct);
        var started = authoritative ? await starter.DispatchAsync(decisions, ct) : 0;

        var summary = new TaskAutoDispatchScanSummary
        {
            WorkspaceId = workspaceId,
            Mode = authoritative ? "authoritative" : "shadow",
            Trigger = trigger,
            StartedAtUtc = startedAt,
            CompletedAtUtc = timeProvider.GetUtcNow(),
            DurationMs = sw.ElapsedMilliseconds,
            AvailabilityRefreshed = availability.Count,
            IdleAgents = availability.Count(item => item.State == AgentAvailabilityState.Idle),
            BusyAgents = availability.Count(item => item.State is not AgentAvailabilityState.Idle and not AgentAvailabilityState.Unknown),
            UnknownAgents = availability.Count(item => item.State == AgentAvailabilityState.Unknown),
            Backlog = backlog.Count,
            RefinementReady = backlog.Count(item => item.Verdict == TaskBacklogRefinementVerdict.ReadyCandidate),
            NeedsRefinement = backlog.Count(item => item.Verdict == TaskBacklogRefinementVerdict.NeedsRefinement),
            Promoted = promoted,
            Candidates = decisions.Count,
            Eligible = decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Eligible),
            Deferred = decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Deferred),
            Denied = decisions.Count(item => item.Verdict == TaskAutoDispatchCandidateVerdict.Denied),
            Started = started,
            Tracked = tracking.Count,
            Healthy = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Healthy),
            Waiting = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Waiting),
            Stalled = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Stalled),
            Inconsistent = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Inconsistent),
            CleanupRequired = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.CleanupRequired),
            Repaired = repairs.Repaired,
            DecisionCodes = decisions
                .GroupBy(item => item.Code, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            RepairCodes = repairs.RepairedByCode,
        };

        logger.LogInformation(
            "[TaskAutoDispatch] trigger={Trigger} mode={Mode} workspace={WorkspaceId} availabilityRefreshed={AvailabilityRefreshed} idle={IdleAgents} busy={BusyAgents} unknown={UnknownAgents} backlog={Backlog} refinementReady={RefinementReady} needsRefinement={NeedsRefinement} promoted={Promoted} candidates={Candidates} eligible={Eligible} started={Started} deferred={Deferred} denied={Denied} tracked={Tracked} healthy={Healthy} waiting={Waiting} stalled={Stalled} inconsistent={Inconsistent} cleanupRequired={CleanupRequired} repaired={Repaired} decisionCodes={DecisionCodes} repairCodes={RepairCodes} durationMs={DurationMs}",
            summary.Trigger,
            summary.Mode,
            summary.WorkspaceId,
            summary.AvailabilityRefreshed,
            summary.IdleAgents,
            summary.BusyAgents,
            summary.UnknownAgents,
            summary.Backlog,
            summary.RefinementReady,
            summary.NeedsRefinement,
            summary.Promoted,
            summary.Candidates,
            summary.Eligible,
            summary.Started,
            summary.Deferred,
            summary.Denied,
            summary.Tracked,
            summary.Healthy,
            summary.Waiting,
            summary.Stalled,
            summary.Inconsistent,
            summary.CleanupRequired,
            summary.Repaired,
            Join(summary.DecisionCodes),
            Join(summary.RepairCodes),
            summary.DurationMs);
        return summary;
    }

    public async Task<TaskAutoDispatchScanSummary> RepairAsync(
        string workspaceId,
        int candidateLimit,
        string trigger,
        CancellationToken ct = default)
    {
        var startedAt = timeProvider.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var tracking = await executionTracker.EvaluateAsync(
            workspaceId,
            Math.Clamp(candidateLimit, 1, 500),
            ct);
        var repairs = await executionRepairCoordinator.RepairAsync(workspaceId, tracking, ct);
        var availability = await RefreshAvailabilityAsync(workspaceId, ct);
        return new TaskAutoDispatchScanSummary
        {
            WorkspaceId = workspaceId,
            Mode = "repair",
            Trigger = trigger,
            StartedAtUtc = startedAt,
            CompletedAtUtc = timeProvider.GetUtcNow(),
            DurationMs = sw.ElapsedMilliseconds,
            AvailabilityRefreshed = availability.Count,
            IdleAgents = availability.Count(item => item.State == AgentAvailabilityState.Idle),
            BusyAgents = availability.Count(item => item.State is not AgentAvailabilityState.Idle and not AgentAvailabilityState.Unknown),
            UnknownAgents = availability.Count(item => item.State == AgentAvailabilityState.Unknown),
            Tracked = tracking.Count,
            Healthy = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Healthy),
            Waiting = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Waiting),
            Stalled = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Stalled),
            Inconsistent = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.Inconsistent),
            CleanupRequired = tracking.Count(item => item.Verdict == TaskExecutionTrackingVerdict.CleanupRequired),
            Repaired = repairs.Repaired,
            RepairCodes = repairs.RepairedByCode,
        };
    }

    private async Task<IReadOnlyList<AgentAvailabilitySnapshot>> RefreshAvailabilityAsync(
        string workspaceId,
        CancellationToken ct)
    {
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var snapshots = new List<AgentAvailabilitySnapshot>(agents.Count);
        foreach (var agent in agents.OrderBy(item => item.AgentId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            snapshots.Add(await availabilityStore.RebuildAsync(workspaceId, agent.AgentId, ct));
        }
        return snapshots;
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

    private static TaskExecutionRepairSummary EmptyRepair(int examined) => new()
    {
        Examined = examined,
        Repaired = 0,
        RepairedByCode = new Dictionary<string, int>(),
    };

    private static string Join(IReadOnlyDictionary<string, int> values) =>
        string.Join(",", values.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));
}

public sealed record TaskAutoDispatchScanSummary
{
    public required string WorkspaceId { get; init; }
    public required string Mode { get; init; }
    public required string Trigger { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public int AvailabilityRefreshed { get; init; }
    public int IdleAgents { get; init; }
    public int BusyAgents { get; init; }
    public int UnknownAgents { get; init; }
    public int Backlog { get; init; }
    public int RefinementReady { get; init; }
    public int NeedsRefinement { get; init; }
    public int Promoted { get; init; }
    public int Candidates { get; init; }
    public int Eligible { get; init; }
    public int Deferred { get; init; }
    public int Denied { get; init; }
    public int Started { get; init; }
    public int Tracked { get; init; }
    public int Healthy { get; init; }
    public int Waiting { get; init; }
    public int Stalled { get; init; }
    public int Inconsistent { get; init; }
    public int CleanupRequired { get; init; }
    public int Repaired { get; init; }
    public IReadOnlyDictionary<string, int> DecisionCodes { get; init; }
        = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> RepairCodes { get; init; }
        = new Dictionary<string, int>();
}
