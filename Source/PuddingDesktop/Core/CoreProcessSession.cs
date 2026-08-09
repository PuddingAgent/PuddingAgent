namespace PuddingDesktop.Core;

/// <summary>
/// Represents a running Core child process session.
/// </summary>
public sealed record CoreProcessSession
{
    public required int ProcessId { get; init; }
    /// <summary>Loopback address used by Desktop for trusted local control traffic.</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>External HTTP listener exposed by the Core child.</summary>
    public Uri? ListenAddress { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public int ExitCode { get; init; }
    public bool HasExited { get; init; }
}
