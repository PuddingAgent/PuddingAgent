using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.Scheduling;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-16 scheduler slice: binds the scheduler cards to Core's TaskSchedulerControlService - the same singleton
/// the HTTP controller uses - so policy CAS, manual scan and repair behave identically on both surfaces.
/// </summary>
internal sealed class DesktopSchedulerSettings(IDesktopKernel kernel) : ISchedulerSettings
{
    private Task<T> Scheduler<T>(string operationId,
        Func<TaskSchedulerControlService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<TaskSchedulerControlService>(), token),
            cancellationToken);

    public Task<SchedulerStatus> GetStatusAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Scheduler("scheduler.status", (service, _) =>
            Task.FromResult(Map(service.GetStatus(workspaceId))), cancellationToken);

    public Task<SchedulerStatus> SavePolicyAsync(
        string workspaceId, SchedulerPolicyEdit edit, CancellationToken cancellationToken = default)
        => Scheduler("scheduler.policy", async (service, token) =>
        {
            var normalized = SchedulerText.Normalize(edit);
            var status = await Guarded(() => service.UpdatePolicyAsync(workspaceId, new TaskSchedulerPolicyUpdate
            {
                ExpectedRevision = normalized.ExpectedRevision,
                Enabled = normalized.Enabled,
                Paused = normalized.Paused,
                Mode = normalized.Mode,
                ScanIntervalSeconds = normalized.ScanIntervalSeconds,
                CandidateLimit = normalized.CandidateLimit,
                MaxStartsPerScan = normalized.MaxStartsPerScan,
                EventDrivenEnabled = normalized.EventDrivenEnabled,
            }, token));
            return Map(status);
        }, cancellationToken);

    public Task<SchedulerStatus> SetPausedAsync(
        string workspaceId, bool paused, int expectedRevision, CancellationToken cancellationToken = default)
        => Scheduler("scheduler.pause", async (service, token) =>
            Map(await Guarded(() => service.SetPausedAsync(workspaceId, paused, expectedRevision, token))),
            cancellationToken);

    public Task<SchedulerScanSummary> RunScanAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Scheduler("scheduler.scan", async (service, token) =>
            Map(await service.RunScanAsync(workspaceId, "admin_manual", allowWhenPaused: true, token)),
            cancellationToken);

    public Task<SchedulerScanSummary> RunRepairAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Scheduler("scheduler.repair", async (service, token) =>
            Map(await service.RunRepairAsync(workspaceId, "admin_manual_repair", token)),
            cancellationToken);

    /// <summary>
    /// Core signals a CAS conflict and a validation refusal with the same exception type, distinguished by code:
    /// a conflict becomes SettingsConflictException so the page can say "prevented an overwrite".
    /// </summary>
    private static async Task<T> Guarded<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (TaskSchedulerControlException exception)
        {
            throw string.Equals(exception.Code, "scheduler_policy_conflict", StringComparison.Ordinal)
                ? new SettingsConflictException(exception.Message)
                : new ArgumentException(exception.Message, nameof(action));
        }
    }

    private static SchedulerStatus Map(TaskSchedulerStatusSnapshot snapshot) => new(
        snapshot.WorkspaceId, snapshot.State,
        new SchedulerPolicy(
            snapshot.Policy.Revision, snapshot.Policy.Enabled, snapshot.Policy.Paused, snapshot.Policy.Mode,
            snapshot.Policy.ScanIntervalSeconds, snapshot.Policy.MinimumIdleSeconds, snapshot.Policy.CandidateLimit,
            snapshot.Policy.MaxStartsPerScan, snapshot.Policy.TrackerStallSeconds, snapshot.Policy.EventDrivenEnabled),
        new SchedulerPrerequisites(
            snapshot.Prerequisites.TaskBoundGoalsEnabled, snapshot.Prerequisites.GoalRunsEnabled,
            snapshot.Prerequisites.GoalContinuationEnabled),
        snapshot.LastScan is { } scan ? Map(scan) : null,
        snapshot.NextScanEstimateUtc, snapshot.LastError ?? "", snapshot.LastFailedAtUtc);

    private static SchedulerScanSummary Map(TaskAutoDispatchScanSummary scan) => new(
        scan.WorkspaceId, scan.Mode, scan.Trigger, scan.ScanId ?? "",
        scan.StartedAtUtc, scan.CompletedAtUtc, scan.DurationMs,
        scan.IdleAgents, scan.BusyAgents, scan.UnknownAgents,
        scan.Backlog, scan.RefinementReady, scan.NeedsRefinement, scan.Promoted,
        scan.Candidates, scan.Eligible, scan.Deferred, scan.Denied, scan.Started, scan.DecisionsRecorded,
        scan.Tracked, scan.Healthy, scan.Waiting, scan.Stalled, scan.Inconsistent, scan.CleanupRequired, scan.Repaired,
        scan.DecisionCodes, scan.RepairCodes);
}
