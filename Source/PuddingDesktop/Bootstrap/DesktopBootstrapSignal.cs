namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Parsed contents of a bootstrap signal file (JSON, UTF-8).
/// Protocol: { "token": "&lt;controlToken&gt;", "action": "rebuild-restart",
///             "yolo": true, "requestedBy": "...", "message": "..." }
/// </summary>
public sealed record DesktopBootstrapSignal
{
    /// <summary>Must equal the current Desktop control token, otherwise the signal is rejected.</summary>
    public string? Token { get; init; }

    /// <summary>Supported action: "rebuild-restart".</summary>
    public string? Action { get; init; }

    /// <summary>When true (and AutoYolo is enabled), write yolo.signal before restarting Core.</summary>
    public bool Yolo { get; init; }

    /// <summary>Optional free-form requester identity, echoed into yolo.signal and the result file.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Optional free-form message, currently informational only.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Result of one bootstrap attempt, written to &lt;SignalPath&gt;.result.json
/// so external tooling has self-evident health feedback.
/// </summary>
public sealed record DesktopBootstrapResult
{
    /// <summary>True only when the build succeeded, Core is running again and no error was recorded.</summary>
    public bool Success { get; init; }

    /// <summary>Action that was requested ("rebuild-restart").</summary>
    public string Action { get; init; } = "rebuild-restart";

    /// <summary>When the signal was first picked up.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the whole flow (including result write) finished.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>dotnet build exit code. Null when the build never ran.</summary>
    public int? BuildExitCode { get; init; }

    /// <summary>Last 30 lines of the build output (stdout + stderr).</summary>
    public List<string> BuildLogTail { get; init; } = [];

    /// <summary>True when Core was confirmed running (Ready) after the restart attempt.</summary>
    public bool CoreRestarted { get; init; }

    /// <summary>True when yolo.signal was written after a successful build.</summary>
    public bool YoloSignalWritten { get; init; }

    /// <summary>Human-readable error list. Empty on a fully successful loop.</summary>
    public List<string> Errors { get; init; } = [];
}
