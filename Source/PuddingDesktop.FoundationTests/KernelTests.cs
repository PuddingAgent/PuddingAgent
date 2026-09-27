using PuddingDesktop.Foundation;
namespace PuddingDesktop.FoundationTests;

public sealed class KernelTests
{
    [Fact]
    public async Task HostRequestedStopReleasesSessionWithoutClosingDesktop()
    {
        var factory = new Factory(); await using var kernel = new InProcessKernel(factory);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kernel.StateChanged += (_, _) => { if (kernel.Snapshot.State == DesktopKernelState.Stopped) stopped.TrySetResult(); };
        await kernel.StartAsync("test-root", default);
        var session = factory.Session;
        session.Shutdown.Cancel();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Disposed);
    }
    [Fact]
    public async Task ConcurrentStartCreatesOneSession_StopDisposesBeforeRestart()
    {
        var factory = new Factory(); await using var kernel = new InProcessKernel(factory);
        await Task.WhenAll(kernel.StartAsync("test-root", default), kernel.StartAsync("test-root", default));
        Assert.Equal(1, factory.Starts); Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StartAsync("different-root", default));
        await kernel.StopAsync(default); Assert.True(factory.Session.Disposed);
        await kernel.StartAsync("test-root", default); Assert.Equal(2, factory.Starts);
    }
    [Fact]
    public async Task StopCancelsStartup_AndFailureCanRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory { BeforeStart = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using var kernel = new InProcessKernel(factory);
        var start = kernel.StartAsync("test-root", default); await started.Task;
        await kernel.StopAsync(default); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(DesktopKernelState.Stopped, kernel.Snapshot.State);
        factory.BeforeStart = null; await kernel.StartAsync("test-root", default);
        Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
    }
    [Fact]
    public async Task FailedStopRetainsHostAndRejectsReplacement()
    {
        var factory = new Factory(); var kernel = new InProcessKernel(factory);
        await kernel.StartAsync("test-root", default); factory.Session.FailStop = true;
        await Assert.ThrowsAsync<IOException>(() => kernel.StopAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StartAsync("test-root", default));
        Assert.Equal(1, factory.Starts); factory.Session.FailStop = false; await kernel.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => kernel.StartAsync("test-root", default));
    }
    private sealed class Factory : IKernelSessionFactory
    {
        public int Starts; public Session Session = new(); public Func<CancellationToken, Task>? BeforeStart;
        public async Task<IKernelSession> StartAsync(string root, CancellationToken ct)
        { Interlocked.Increment(ref Starts); if (BeforeStart != null) await BeforeStart(ct); return Session = new(); }
    }
    private sealed class Session : IKernelSession
    {
        public readonly CancellationTokenSource Shutdown = new();
        public CancellationToken Stopping => Shutdown.Token;
        public bool Disposed; public bool FailStop;
        public Uri WorkbenchAddress => new("http://127.0.0.1:12345");
        public Task StopAsync(CancellationToken ct) => FailStop ? Task.FromException(new IOException()) : Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
