namespace PuddingDesktop.Core;

/// <summary>
/// Manages the Core (PuddingAgent) child process lifecycle.
/// Desktop launches Core via Process.Start and communicates via
/// stdout Ready signal + HTTP control endpoints.
/// </summary>
public interface ICoreProcessSupervisor : IAsyncDisposable
{
    CoreProcessState State { get; }
    CoreProcessSession? CurrentSession { get; }
    CoreProcessLogBuffer LogBuffer { get; }

    event EventHandler<CoreProcessStateChangedEventArgs>? StateChanged;
    event EventHandler<CoreProcessExitedEventArgs>? UnexpectedExit;

    Task<CoreProcessSession> StartAsync(
        CoreProcessStartOptions options,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
