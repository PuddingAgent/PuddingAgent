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
    [Fact]
    public async Task StartupAttemptReceivesKernelPhasesAndExecutionMilestone()
    {
        var factory = new Factory();
        await using var kernel = new InProcessKernel(factory);
        var attempt = StartupAttempts.Begin("test-root", new StartupAttemptOrigin("kernel-1", 1, "test", DateTimeOffset.UtcNow));

        await kernel.StartAsync("test-root", default, attempt);

        var evidence = attempt.Complete();
        // A successful start must not look aborted: the first real evidence run showed this exact
        // mistake (phase disposed without reporting completion) and it was fixed here.
        Assert.All(evidence.Phases, phase => Assert.Equal(StartupPhaseOutcome.Completed, phase.Outcome));
        Assert.Contains(evidence.Phases, phase => phase.Name == StartupPhases.KernelGate);
        Assert.Contains(evidence.Phases, phase => phase.Name == StartupPhases.KernelCoreStart);
        Assert.Equal([StartupMilestone.ExecutionReady], evidence.Milestones.Select(item => item.Milestone));
        Assert.Equal("test-root", evidence.DataRoot);
        Assert.Empty(evidence.Violations);
    }

    [Fact]
    public async Task FailedKernelStartLeavesItsPhaseAbortedAndNoExecutionMilestone()
    {
        var factory = new Factory { BeforeStart = _ => throw new InvalidOperationException("core refused") };
        await using var kernel = new InProcessKernel(factory);
        var attempt = StartupAttempts.Begin("test-root", new StartupAttemptOrigin("kernel-2", 1, "test", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StartAsync("test-root", default, attempt));

        var evidence = attempt.Fail("InvalidOperationException");
        Assert.Equal(StartupPhaseOutcome.Completed, Assert.Single(evidence.Phases, phase => phase.Name == StartupPhases.KernelGate).Outcome);
        Assert.Equal(StartupPhaseOutcome.Aborted, Assert.Single(evidence.Phases, phase => phase.Name == StartupPhases.KernelCoreStart).Outcome);
        Assert.Empty(evidence.Milestones);
    }

    [Fact]
    public async Task KernelWithoutAnAttemptStartsAndStopsTheSameWay()
    {
        var factory = new Factory();
        await using var kernel = new InProcessKernel(factory);
        await kernel.StartAsync("test-root", default);
        Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
        await kernel.StopAsync(default);
        Assert.Equal(1, factory.Starts);
    }

    private sealed class Factory : IKernelSessionFactory
    {
        public int Starts; public Session Session = new(); public Func<CancellationToken, Task>? BeforeStart;
        public async Task<IKernelSession> StartAsync(string root, CancellationToken ct, IStartupAttempt? startup = null)
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
