namespace PuddingDesktop.Core;

/// <summary>
/// Options for starting a Core child process.
/// </summary>
public sealed record CoreProcessStartOptions
{
    /// <summary>Path to PuddingAgent.exe (resolved by CoreExecutableResolver).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Data root directory for Core.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Dynamic loopback port (0 = OS assigns).</summary>
    public int Port { get; init; }

    /// <summary>Desktop process ID for parent monitoring.</summary>
    public required int ParentProcessId { get; init; }

    /// <summary>Control token for POST /internal/desktop/shutdown. NOT passed to Core command line.</summary>
    public required string ControlToken { get; init; }

    /// <summary>Maximum time to wait for Ready signal.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum time to wait for graceful shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
