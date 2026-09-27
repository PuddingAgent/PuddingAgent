namespace PuddingDesktop.Core;

/// <summary>
/// Observable state of the Core child process.
/// </summary>
public enum CoreProcessState
{
    /// <summary>Not started yet.</summary>
    Idle,

    /// <summary>Process is starting, waiting for Ready signal.</summary>
    Starting,

    /// <summary>Ready signal received; Core is healthy and bound.</summary>
    Ready,

    /// <summary>Shutdown requested, waiting for graceful exit.</summary>
    Stopping,

    /// <summary>Process exited normally.</summary>
    Stopped,

    /// <summary>Process exited unexpectedly or failed to start.</summary>
    Failed,
}
