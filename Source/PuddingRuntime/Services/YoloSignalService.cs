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

        var workspaceRoot = AppDomain.CurrentDomain.BaseDirectory;
        // Walk up from bin/Debug/net10.0 to the repo root (where checkpoint.json lives).
        for (int i = 0; i < 5; i++)
        {
            var candidate = Path.Combine(workspaceRoot, "yolo.signal");
            if (File.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)!))
            {
                _signalPath = candidate;
                break;
            }
            workspaceRoot = Path.GetFullPath(Path.Combine(workspaceRoot, ".."));
        }

        // Fallback: put it next to checkpoint.json
        if (string.IsNullOrEmpty(_signalPath))
        {
            _signalPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "yolo.signal"));
        }

        _logger.LogInformation("[YoloSignal] Watching {Path}", _signalPath);
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
