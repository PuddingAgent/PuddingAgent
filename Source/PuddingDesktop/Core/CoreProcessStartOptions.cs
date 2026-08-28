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

    /// <summary>Fixed port exposed on all IPv4 interfaces by the Core child.</summary>
    public int Port { get; init; } = PuddingCode.Configuration.PuddingDesktopCoreConfig.DefaultPort;

    /// <summary>Desktop process ID for parent monitoring.</summary>
    public required int ParentProcessId { get; init; }

    /// <summary>Control token for POST /internal/desktop/shutdown. NOT passed to Core command line.</summary>
    public required string ControlToken { get; init; }

    /// <summary>
    /// Maximum silence between process start/progress messages while waiting
    /// for Ready. A separate bounded hard timeout prevents infinite renewal.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum time to wait for graceful shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Overrides ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT for the Core child.
    /// Null (default) forces Production; debug mode passes Development to run
    /// the source-built backend like dev-up does.
    /// </summary>
    public string? EnvironmentName { get; init; }
}
