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
    TaskSchedulerScanRunStore scanRunStore,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    ILogger<TaskAutoDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // §7.2-4 启动恢复：把上一进程（host_boot_id ≠ 当前 boot）遗留的 running 扫描判为
        // abandoned。一次性、与 enabled/mode 无关——遗留事实无论调度开关状态都应收敛；
        // 恢复自身失败不阻断 worker（下轮 boot 重试），只记告警。
        try
        {
            var abandoned = await scanRunStore.MarkAbandonedAsync(
                TaskSchedulerScanRunStore.HostBootId, stoppingToken);
            if (abandoned > 0)
                logger.LogInformation(
                    "[TaskAutoDispatch] startup recovery: {Abandoned} leftover running scan run(s) -> abandoned",
                    abandoned);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TaskAutoDispatch] startup recovery: mark abandoned scan runs failed");
        }

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
