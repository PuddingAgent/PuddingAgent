using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Low-frequency recovery scanner. The actual round lives in
/// <see cref="TaskAutoDispatchScanRunner"/> so scheduled and user-triggered scans
/// have identical ordering, repair semantics and safety fences.
/// </summary>
public sealed class TaskAutoDispatchWorker(
    TaskSchedulerControlService control,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    ILogger<TaskAutoDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastContainment = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            var workspaces = (current.WorkspaceIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var containment = !current.Enabled
                ? "disabled"
                : workspaces.Length == 0
                    ? "no_workspace"
                    : "running";
            if (!string.Equals(lastContainment, containment, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "[TaskAutoDispatch] worker state={State} mode={Mode} workspaces={Workspaces}",
                    containment,
                    current.Mode,
                    string.Join(",", workspaces));
                lastContainment = containment;
            }

            if (current.Enabled)
            {
                foreach (var workspaceId in workspaces)
                {
                    if (current.PausedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
                        continue;
                    try
                    {
                        await control.RunScanAsync(
                            workspaceId,
                            "recovery_scan",
                            allowWhenPaused: false,
                            stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (TaskSchedulerControlException ex) when (
                        ex.Code is "scheduler_scan_in_progress" or "scheduler_paused")
                    {
                        logger.LogDebug(
                            "[TaskAutoDispatch] scan skipped workspace={WorkspaceId} code={Code}",
                            workspaceId,
                            ex.Code);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "[TaskAutoDispatch] scan failed workspace={WorkspaceId} mode={Mode}",
                            workspaceId,
                            current.Mode);
                    }
                }
            }

            try
            {
                await control.WaitForSignalOrDelayAsync(
                    current.Enabled
                        ? current.ScanInterval
                        : TimeSpan.FromSeconds(30),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
