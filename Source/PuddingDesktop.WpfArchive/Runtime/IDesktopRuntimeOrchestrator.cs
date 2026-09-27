using PuddingDesktop.Core;

namespace PuddingDesktop.Runtime;

public interface IDesktopRuntimeOrchestrator : IAsyncDisposable
{
    DesktopRuntimeSnapshot Snapshot { get; }
    event EventHandler<DesktopRuntimeChangedEventArgs>? Changed;

    void Configure(CoreProcessStartOptions options, CoreRestartPolicy policy);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task RestartAsync(CancellationToken cancellationToken);
    Task SetAutoRestartAsync(bool enabled, CancellationToken cancellationToken);
}
