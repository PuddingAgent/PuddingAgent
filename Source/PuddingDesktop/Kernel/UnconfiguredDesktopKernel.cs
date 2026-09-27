using PuddingDesktop.Foundation;

namespace PuddingDesktop.Kernel;

/// <summary>Honest unavailable adapter: the skeleton must never report a successful Core start.</summary>
internal sealed class UnconfiguredDesktopKernel : IDesktopKernel
{
    public DesktopKernelSnapshot Snapshot { get; } = new(DesktopKernelState.NotConfigured, "Core DLL 尚未装配");
    public Task StartAsync(string dataRoot, CancellationToken cancellationToken)
        => Task.FromException(new InvalidOperationException(Snapshot.Description));
    public Task StopAsync(CancellationToken cancellationToken)
        => Task.FromException(new InvalidOperationException(Snapshot.Description));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
