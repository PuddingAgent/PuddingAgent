namespace PuddingDesktop.Core;

/// <summary>
/// Represents a running Core child process session.
/// </summary>
public sealed record CoreProcessSession
{
    public required int ProcessId { get; init; }
    public required Uri BaseAddress { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public int ExitCode { get; init; }
    public bool HasExited { get; init; }
}
