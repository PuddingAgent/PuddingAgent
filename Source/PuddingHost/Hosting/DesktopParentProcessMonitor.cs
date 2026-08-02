using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace PuddingHost.Hosting;

/// <summary>
/// Monitors the parent Desktop process and shuts down Core when the parent exits.
/// Registered as an IHostedService in DesktopChild mode.
/// </summary>
public sealed class DesktopParentProcessMonitor : BackgroundService
{
    private readonly int _parentPid;
    private readonly IHostApplicationLifetime _appLifetime;

    public DesktopParentProcessMonitor(int parentPid, IHostApplicationLifetime appLifetime)
    {
        _parentPid = parentPid;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var parent = Process.GetProcessById(_parentPid);
                if (parent.HasExited)
                {
                    Console.WriteLine("[DesktopChild] Parent process exited. Shutting down Core.");
                    _appLifetime.StopApplication();
                    return;
                }
            }
            catch
            {
                // Parent process no longer exists
                Console.WriteLine("[DesktopChild] Parent process not found. Shutting down Core.");
                _appLifetime.StopApplication();
                return;
            }

            try
            {
                await Task.Delay(2000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
