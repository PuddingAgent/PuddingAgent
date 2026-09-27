using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-00 baseline: readiness refusal, stop-time refusal, cancel/drain, generation and selection
/// invalidation, and version conflict. These run without a Core host.
/// </summary>
public sealed class SettingsOperationTests
{
    [Theory]
    [InlineData(DesktopKernelState.NotConfigured, SettingsUnavailable.KernelNotConfigured)]
    [InlineData(DesktopKernelState.Stopped, SettingsUnavailable.KernelStopped)]
    [InlineData(DesktopKernelState.Starting, SettingsUnavailable.KernelStarting)]
    [InlineData(DesktopKernelState.Stopping, SettingsUnavailable.KernelStopping)]
    [InlineData(DesktopKernelState.Failed, SettingsUnavailable.KernelFailed)]
    public async Task KernelIsNotReady_OperationIsRefusedWithReason(DesktopKernelState state, SettingsUnavailable expected)
    {
        var gate = new SettingsOperationGate();
        gate.SetState(state);
        var ran = false;
        var error = await Assert.ThrowsAsync<SettingsUnavailableException>(
            () => gate.RunAsync((_, _) => { ran = true; return Task.FromResult(true); }, default));
        Assert.Equal(expected, error.Reason);
        Assert.False(ran);
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public async Task Stopping_RefusesNewWorkAndDrainsAcceptedWork()
    {
        var gate = new SettingsOperationGate();
        gate.SetState(DesktopKernelState.Ready);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = gate.RunAsync(async (stamp, _) =>
        {
            await release.Task;
            return stamp.KernelEpoch;
        }, default);
        Assert.Equal(1, gate.OutstandingOperations);

        gate.SetState(DesktopKernelState.Stopping);
        var refused = await Assert.ThrowsAsync<SettingsUnavailableException>(
            () => gate.RunAsync((_, _) => Task.FromResult(0), default));
        Assert.Equal(SettingsUnavailable.KernelStopping, refused.Reason);

        var drain = gate.DrainAsync(default);
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await drain;
        Assert.True(accepted.IsCompletedSuccessfully);
        Assert.Equal(0, gate.OutstandingOperations);
    }

    [Fact]
    public async Task AcceptedWorkObservesCallerCancellation()
    {
        var gate = new SettingsOperationGate();
        gate.SetState(DesktopKernelState.Ready);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = gate.RunAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await gate.DrainAsync(default);
        Assert.Equal(0, gate.OutstandingOperations);
    }

    [Fact]
    public void NewKernelGenerationInvalidatesEarlierStampAndClearsSelection()
    {
        var gate = new SettingsOperationGate();
        gate.SetState(DesktopKernelState.Ready);
        Assert.True(gate.SetSelection(new SettingsSelection("ws-a", "agent-1")));
        var stamp = gate.Capture();
        Assert.True(stamp.Selection.IsSpecified);
        gate.EnsureCurrent(stamp);

        gate.KernelChanged(DesktopKernelState.Starting);
        Assert.False(gate.IsCurrent(stamp));
        Assert.Equal(SettingsSelection.None, gate.Selection);
        var error = Assert.Throws<SettingsSupersededException>(() => gate.EnsureCurrent(stamp));
        Assert.Contains("作废", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionChangeInvalidatesEarlierStamp_UnchangedSelectionKeepsIt()
    {
        var gate = new SettingsOperationGate();
        gate.SetState(DesktopKernelState.Ready);
        gate.SetSelection(new SettingsSelection("ws-a", "agent-1"));
        var stamp = gate.Capture();

        Assert.False(gate.SetSelection(new SettingsSelection("ws-a", "agent-1")));
        Assert.True(gate.IsCurrent(stamp));

        Assert.True(gate.SetSelection(new SettingsSelection("ws-a", "agent-2")));
        Assert.False(gate.IsCurrent(stamp));
        Assert.True(gate.IsCurrent(gate.Capture()));
    }

    [Fact]
    public void VersionConflictIsReportedAndUnversionedWritesPassThrough()
    {
        SettingsVersionGuard.EnsureCurrent(null, 7, "保留策略");
        SettingsVersionGuard.EnsureCurrent(7, 7, "保留策略");
        var error = Assert.Throws<SettingsConflictException>(() => SettingsVersionGuard.EnsureCurrent(6, 7, "保留策略"));
        Assert.Contains("当前版本 7", error.Message, StringComparison.Ordinal);
        Assert.Contains("本次提交版本 6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundKernelStillAllowsBrowsingBeforeAnyGeneration()
    {
        var gate = new SettingsOperationGate();
        Assert.Equal(DesktopKernelState.Stopped, gate.State);
        Assert.True(gate.Capture().IsUnbound);
        gate.KernelChanged(DesktopKernelState.Starting);
        Assert.False(gate.Capture().IsUnbound);
    }

    [Fact]
    public async Task KernelWithoutSettingsHost_ReportsUnavailableInsteadOfFakingSuccess()
    {
        var factory = new LifecycleFactory();
        await using var kernel = new InProcessKernel(factory);
        var error = await Assert.ThrowsAsync<SettingsUnavailableException>(
            () => kernel.RunSettingsAsync("probe", (_, _) => Task.FromResult(0)));
        Assert.Equal(SettingsUnavailable.KernelStopped, error.Reason);

        await kernel.StartAsync("test-root", default);
        var noHost = await Assert.ThrowsAsync<SettingsUnavailableException>(
            () => kernel.RunSettingsAsync("probe", (_, _) => Task.FromResult(0)));
        Assert.Equal(SettingsUnavailable.KernelNotConfigured, noHost.Reason);
    }

    [Fact]
    public async Task KernelRunsSettingsThroughHostAndRefusesAfterStopRequested()
    {
        var factory = new LifecycleFactory { Host = new FakeHost() };
        await using var kernel = new InProcessKernel(factory);
        await kernel.StartAsync("test-root", default);
        var value = await kernel.RunSettingsAsync("probe", (scope, _) => Task.FromResult(scope.Services is not null ? 42 : -1));
        Assert.Equal(42, value);

        await kernel.StopAsync(default);
        var refused = await Assert.ThrowsAsync<SettingsUnavailableException>(
            () => kernel.RunSettingsAsync("probe", (_, _) => Task.FromResult(0)));
        Assert.Equal(SettingsUnavailable.KernelStopped, refused.Reason);
    }

    [Fact]
    public async Task KernelDrainsAcceptedSettingsBeforeReleasingSession()
    {
        var host = new FakeHost();
        var factory = new LifecycleFactory { Host = host };
        await using var kernel = new InProcessKernel(factory);
        await kernel.StartAsync("test-root", default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = kernel.RunSettingsAsync("slow", async (_, _) => { await release.Task; return 1; });
        Assert.Equal(1, kernel.Settings.OutstandingOperations);

        var stop = kernel.StopAsync(default);
        await Task.Delay(30);
        Assert.False(host.Session.Stopped);
        release.SetResult();
        Assert.Equal(1, await accepted);
        await stop;
        Assert.True(host.Session.Stopped);
    }

    [Fact]
    public void LocalDesktopIdentityIsFixedAndNotConstructedFromInput()
    {
        Assert.Equal("single-user", LocalDesktopIdentity.UserId);
        Assert.True(LocalDesktopIdentity.Owns(LocalDesktopIdentity.UserId));
        Assert.True(LocalDesktopIdentity.Owns("single-user"));
        Assert.False(LocalDesktopIdentity.Owns("Single-User"));
        Assert.False(LocalDesktopIdentity.Owns(" single-user"));
        Assert.False(LocalDesktopIdentity.Owns("admin"));
        Assert.False(LocalDesktopIdentity.Owns(""));
        Assert.False(LocalDesktopIdentity.Owns(null));
    }

    private sealed class LifecycleFactory : IKernelSessionFactory
    {
        public FakeHost? Host;
        public Task<IKernelSession> StartAsync(string dataRoot, CancellationToken cancellationToken)
        {
            IKernelSession session = Host is null ? new PlainSession() : Host.Session;
            return Task.FromResult(session);
        }
    }

    private sealed class FakeHost { public HostSession Session { get; } = new(); }

    private sealed class PlainSession : IKernelSession
    {
        public Uri WorkbenchAddress => new("http://127.0.0.1:1/");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HostSession : IKernelSession, ISettingsOperationHost
    {
        public bool Stopped { get; private set; }
        public Uri WorkbenchAddress => new("http://127.0.0.1:1/");
        public Task StopAsync(CancellationToken cancellationToken) { Stopped = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<T> RunAsync<T>(Func<ISettingsScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
            => body(new FakeScope(), cancellationToken);
    }

    private sealed class FakeScope : ISettingsScope
    {
        public IServiceProvider Services { get; } = new EmptyProvider();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
