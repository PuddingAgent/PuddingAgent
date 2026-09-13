using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    TaskSchedulerDecisionStore decisionStore,
    TaskSchedulerScanRunStore scanRunStore,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
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
        // Staged mode（五值）：shadow 只评估；authoritative-single/-bounded/-authoritative 共享派发线；
        // disabled 不应进入扫描（控制面已拦，双保险拒绝）。
        var normalizedMode = TaskAutoDispatchOptions.NormalizeMode(mode);
        var authoritative = TaskAutoDispatchOptions.IsAuthoritativeMode(normalizedMode);
        if (!authoritative && !TaskAutoDispatchOptions.IsShadowMode(normalizedMode))
            throw new InvalidOperationException("scheduler_mode_invalid");

        var startedAt = timeProvider.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var limit = Math.Clamp(candidateLimit, 1, 500);

        // §7.2-1：扫描开始先落 running 行；scanId 由 scan_runs 表生成并贯穿决策持久化，
        // decisions.scan_id 与 scan_runs.scan_id 由此可追溯互查。开始失败即扫描中止（fail closed）。
        var scanId = await scanRunStore.TryStartAsync(
            workspaceId,
            trigger,
            normalizedMode,
            options.CurrentValue.PolicyRevision,
            TaskSchedulerScanRunStore.HostBootId,
            ct);
        try
        {
            var summary = await EvaluateCoreAsync(
                workspaceId, normalizedMode, authoritative, limit, startedAt, sw, scanId, trigger, ct);

            // §7.2-2：成功终态（含 candidates=0 空扫描）；只存汇总与稳定原因分布，不双写明细。
            await scanRunStore.CompleteAsync(scanId, new TaskSchedulerScanRunCompletion
            {
                CompletedAtUtc = summary.CompletedAtUtc,
                DurationMs = summary.DurationMs,
                AvailabilityRefreshed = summary.AvailabilityRefreshed,
                IdleAgents = summary.IdleAgents,
                BusyAgents = summary.BusyAgents,
                UnknownAgents = summary.UnknownAgents,
                Backlog = summary.Backlog,
                Candidates = summary.Candidates,
                Eligible = summary.Eligible,
                Started = summary.Started,
                Tracked = summary.Tracked,
                Repaired = summary.Repaired,
                DecisionCodesJson = TaskSchedulerScanRunStore.SerializeCodeDistribution(summary.DecisionCodes),
                RepairCodesJson = TaskSchedulerScanRunStore.SerializeCodeDistribution(summary.RepairCodes),
            }, ct);
            return summary;
        }
        catch (Exception ex)
        {
            // §7.2-3：失败终态后重新抛出（禁止吞错）；终态持久化自身失败不得掩盖原异常。
            try
            {
                await scanRunStore.FailAsync(
                    scanId,
                    ex is TaskSchedulerControlException control ? control.Code : ex.GetType().Name,
                    ex.Message,
                    ct);
            }
            catch (Exception persistEx)
            {
                logger.LogError(
                    persistEx,
                    "[TaskAutoDispatch] scan run failure persistence failed scanId={ScanId}",
                    scanId);
            }

            throw;
        }
    }

    private async Task<TaskAutoDispatchScanSummary> EvaluateCoreAsync(
        string workspaceId,
        string normalizedMode,
        bool authoritative,
        int limit,
        DateTimeOffset startedAt,
        Stopwatch sw,
        string scanId,
        string trigger,
        CancellationToken ct)
    {
        // Ownership reconciliation must run before availability and selection.
        var tracking = await executionTracker.EvaluateAsync(workspaceId, limit, ct);
        var repairs = authoritative
            ? await executionRepairCoordinator.RepairAsync(workspaceId, tracking, ct)
            : EmptyRepair(tracking.Count);
        var availability = await RefreshAvailabilityAsync(workspaceId, ct);
        var backlog = await backlogRefinementEvaluator.EvaluateAsync(workspaceId, limit, ct);
        var promoted = authoritative ? await PromoteBacklogAsync(backlog, ct) : 0;
        var decisions = await evaluator.EvaluateAsync(workspaceId, limit, ct);
        // 缺口 2 写点①/②：candidate 与 refinement 决策持久化（shadow 与 authoritative 都落，
        // mode 列分流；UNIQUE(scan_id, task_id, phase) 保证同 scan 重放幂等）。
        // 写点③：defer/deny 门写回 workspace_tasks.next_eligible_at_utc（只前推不回拨）。
        // 决策持久化是观测能力，落库失败不拖垮扫描轮，记日志后继续。
        int recorded;
        int refinementRecorded;
        int writeBacks;
        try
        {
            recorded = await decisionStore.RecordCandidateDecisionsAsync(
                workspaceId, normalizedMode, scanId, decisions, ct);
            refinementRecorded = await decisionStore.RecordRefinementDecisionsAsync(
                workspaceId, normalizedMode, scanId, backlog, ct);
            writeBacks = await decisionStore.ApplyNextEligibleWriteBackAsync(workspaceId, decisions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[TaskAutoDispatch] decision persistence failed workspace={WorkspaceId} scanId={ScanId}",
                workspaceId,
                scanId);
            recorded = 0;
            refinementRecorded = 0;
            writeBacks = 0;
        }
        // 缺口 3：上限统一走 EffectiveMaxStartsPerScan（authoritative-single 强制 1，其余 clamp 配置值），
        // 与事件驱动 Coordinator（§5.3 步骤 6）同一归一语义；shadow 不派发。
        var effectiveMaxStarts = TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(options.CurrentValue);
        var started = authoritative ? await starter.DispatchAsync(decisions, effectiveMaxStarts, ct) : 0;

        var summary = new TaskAutoDispatchScanSummary
        {
            WorkspaceId = workspaceId,
            Mode = normalizedMode,
            ScanId = scanId,
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
            DecisionsRecorded = recorded,
            RefinementRecorded = refinementRecorded,
            NextEligibleWriteBacks = writeBacks,
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
    public string? ScanId { get; init; }
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
    public int DecisionsRecorded { get; init; }
    public int RefinementRecorded { get; init; }
    public int NextEligibleWriteBacks { get; init; }
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
