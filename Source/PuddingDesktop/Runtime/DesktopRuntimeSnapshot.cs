using PuddingDesktop.Core;

namespace PuddingDesktop.Runtime;

public enum DesktopRuntimeState
{
    Idle,
    Starting,
    Ready,
    Stopping,
    Stopped,
    RestartScheduled,
    Failed,
    CircuitOpen,
}

public sealed record DesktopRuntimeSnapshot
{
    public DesktopRuntimeState State { get; init; } = DesktopRuntimeState.Idle;
    public CoreProcessSession? Session { get; init; }
    public int? LastProcessId { get; init; }
    public int? LastExitCode { get; init; }
    public DateTimeOffset? LastExitAt { get; init; }
    public DateTimeOffset? NextRestartAt { get; init; }
    public int RestartAttemptsInWindow { get; init; }
    public bool AutoRestartEnabled { get; init; } = true;
    public bool UserStopRequested { get; init; }
    public string? LastError { get; init; }
}

public sealed class DesktopRuntimeChangedEventArgs(
    DesktopRuntimeSnapshot previous,
    DesktopRuntimeSnapshot current) : EventArgs
{
    public DesktopRuntimeSnapshot Previous { get; } = previous;
    public DesktopRuntimeSnapshot Current { get; } = current;
}
