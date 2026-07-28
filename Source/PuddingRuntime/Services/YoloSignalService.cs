using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// Monitors the workspace root for a <c>yolo.signal</c> file written by dev-up.py
/// after a restart. When the file appears, activates YOLO mode in-process
/// (no HTTP / auth required) and deletes the signal.
/// </summary>
public sealed class YoloSignalService : BackgroundService
{
    private readonly IRuntimeControlService _runtimeControl;
    private readonly ILogger<YoloSignalService> _logger;
    private readonly string _signalPath;

    public YoloSignalService(
        IRuntimeControlService runtimeControl,
        ILogger<YoloSignalService> logger)
    {
        _runtimeControl = runtimeControl;
        _logger = logger;
        _signalPath = ResolveSignalPath(
            Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT"),
            AppDomain.CurrentDomain.BaseDirectory);

        _logger.LogInformation("[YoloSignal] Watching {Path}", _signalPath);
    }

    internal static string ResolveSignalPath(string? repositoryRoot, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
            return Path.Combine(Path.GetFullPath(repositoryRoot), "yolo.signal");

        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "dev-up.py"))
                || File.Exists(Path.Combine(current.FullName, "checkpoint.json")))
            {
                return Path.Combine(current.FullName, "yolo.signal");
            }
        }

        return Path.Combine(Path.GetFullPath(baseDirectory), "yolo.signal");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_signalPath))
                {
                    string? content = null;
                    try
                    {
                        content = await File.ReadAllTextAsync(_signalPath, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[YoloSignal] Failed to read signal file");
                    }

                    _runtimeControl.SetMode(
                        RuntimeExecutionMode.Yolo,
                        $"auto-yolo from dev-up.py signal; content={content ?? "(empty)"}");

                    try
                    {
                        File.Delete(_signalPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[YoloSignal] Failed to delete signal file");
                    }

                    _logger.LogWarning(
                        "[YoloSignal] Activated YOLO mode via file signal. Content: {Content}",
                        content ?? "(empty)");

                    // Once activated, this service has done its job.
                    // Keep monitoring in case of future restarts.
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[YoloSignal] Error in monitor loop");
            }

            await Task.Delay(1_000, stoppingToken);
        }
    }
}
