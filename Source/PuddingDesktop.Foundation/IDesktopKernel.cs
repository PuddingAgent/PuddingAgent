namespace PuddingDesktop.Foundation;

public enum DesktopKernelState { NotConfigured, Stopped, Starting, Ready, Stopping, Failed }

public sealed record DesktopKernelSnapshot(DesktopKernelState State, string Description, Uri? WorkbenchAddress = null);

/// <summary>
/// Lifecycle boundary for the future in-process Core DLL adapter.
/// Implementations belong to composition, never WinUI views; no UI dispatcher or business types cross this port.
/// </summary>
public interface IDesktopKernel : IAsyncDisposable
{
    DesktopKernelSnapshot Snapshot { get; }
    event EventHandler? StateChanged;
    /// <summary>
    /// Starts the in-process Core and records the phases it owns into <paramref name="startup"/>.
    /// The caller owns that attempt: it decides the outcome (complete/fail/cancel) and persists the
    /// evidence, because only the caller sees the shell-side milestones around the kernel.
    /// </summary>
    Task StartAsync(string dataRoot, CancellationToken cancellationToken, IStartupAttempt? startup = null);
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kernel generation, bound selection and outstanding-operation accounting for settings pages.
    /// Reads are free even when Core is not ready so every category stays browsable.
    /// </summary>
    SettingsOperationGate Settings { get; }

    /// <summary>
    /// Runs one settings operation inside an isolated Core scope. Refuses (never fakes) when Core
    /// is not ready or is stopping, and invalidates the result when the generation/selection moved on.
    /// </summary>
    Task<T> RunSettingsAsync<T>(string operationId,
        Func<ISettingsScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken = default);
}
