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
    Task StartAsync(string dataRoot, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
