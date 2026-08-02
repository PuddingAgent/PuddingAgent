using PuddingDesktop.Core;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class DesktopRuntimeOrchestratorTests
{
    [Fact]
    public async Task StartAndStop_UsesSupervisorWithoutAutomaticRestart()
    {
        var supervisor = new FakeCoreProcessSupervisor();
        await using var orchestrator = new DesktopRuntimeOrchestrator(supervisor);
        orchestrator.Configure(CreateOptions(), CreatePolicy());

        await orchestrator.StartAsync(CancellationToken.None);
        await orchestrator.StopAsync(CancellationToken.None);
        supervisor.RaiseUnexpectedExit(exitCode: 9);
        await Task.Delay(30);

        Assert.Equal(1, supervisor.StartCalls);
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal(DesktopRuntimeState.Stopped, orchestrator.Snapshot.State);
        Assert.True(orchestrator.Snapshot.UserStopRequested);
    }

    [Fact]
    public async Task UnexpectedExit_RestartsWhenPolicyAllows()
    {
        var supervisor = new FakeCoreProcessSupervisor();
        await using var orchestrator = new DesktopRuntimeOrchestrator(supervisor);
        orchestrator.Configure(CreateOptions(), CreatePolicy());
        await orchestrator.StartAsync(CancellationToken.None);

        supervisor.RaiseUnexpectedExit(exitCode: 17);
        await WaitUntilAsync(() => supervisor.StartCalls == 2);

        Assert.Equal(DesktopRuntimeState.Ready, orchestrator.Snapshot.State);
        Assert.Equal(1, orchestrator.Snapshot.RestartAttemptsInWindow);
        Assert.Equal(17, orchestrator.Snapshot.LastExitCode);
    }

    [Fact]
    public async Task RepeatedRecoveryFailure_OpensCircuitBreaker()
    {
        var supervisor = new FakeCoreProcessSupervisor();
        await using var orchestrator = new DesktopRuntimeOrchestrator(supervisor);
        orchestrator.Configure(CreateOptions(), CreatePolicy(maxAttempts: 3));
        await orchestrator.StartAsync(CancellationToken.None);
        supervisor.FailuresRemaining = 10;

        supervisor.RaiseUnexpectedExit(exitCode: 23);
        await WaitUntilAsync(() => orchestrator.Snapshot.State == DesktopRuntimeState.CircuitOpen);

        Assert.Equal(4, supervisor.StartCalls);
        Assert.Equal(3, orchestrator.Snapshot.RestartAttemptsInWindow);
        Assert.Contains("熔断", orchestrator.Snapshot.LastError);
    }

    [Fact]
    public async Task Stop_CancelsScheduledRecovery()
    {
        var supervisor = new FakeCoreProcessSupervisor();
        await using var orchestrator = new DesktopRuntimeOrchestrator(supervisor);
        orchestrator.Configure(
            CreateOptions(),
            CreatePolicy(initialDelaySeconds: 30, maxDelaySeconds: 30));
        await orchestrator.StartAsync(CancellationToken.None);

        supervisor.RaiseUnexpectedExit(exitCode: 31);
        await WaitUntilAsync(() => orchestrator.Snapshot.State == DesktopRuntimeState.RestartScheduled);
        await orchestrator.StopAsync(CancellationToken.None);
        await Task.Delay(30);

        Assert.Equal(1, supervisor.StartCalls);
        Assert.Equal(DesktopRuntimeState.Stopped, orchestrator.Snapshot.State);
    }

    private static CoreRestartPolicy CreatePolicy(
        int maxAttempts = 3,
        int initialDelaySeconds = 0,
        int maxDelaySeconds = 0) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        WindowSeconds = 60,
        InitialDelaySeconds = initialDelaySeconds,
        MaxDelaySeconds = maxDelaySeconds,
    };

    private static CoreProcessStartOptions CreateOptions() => new()
    {
        ExecutablePath = "PuddingAgent.exe",
        DataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "runtime-tests"),
        ParentProcessId = Environment.ProcessId,
        ControlToken = "test-token",
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    private sealed class FakeCoreProcessSupervisor : ICoreProcessSupervisor
    {
        private int _nextPid = 1000;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int FailuresRemaining { get; set; }
        public CoreProcessState State { get; private set; } = CoreProcessState.Idle;
        public CoreProcessSession? CurrentSession { get; private set; }
        public CoreProcessLogBuffer LogBuffer { get; } = new();

        public event EventHandler<CoreProcessStateChangedEventArgs>? StateChanged;
        public event EventHandler<CoreProcessExitedEventArgs>? UnexpectedExit;

        public Task<CoreProcessSession> StartAsync(
            CoreProcessStartOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                State = CoreProcessState.Failed;
                throw new InvalidOperationException("simulated startup failure");
            }

            var previous = State;
            State = CoreProcessState.Ready;
            CurrentSession = new CoreProcessSession
            {
                ProcessId = ++_nextPid,
                BaseAddress = new Uri($"http://127.0.0.1:{_nextPid}"),
                StartedAt = DateTimeOffset.UtcNow,
                ReadyAt = DateTimeOffset.UtcNow,
            };
            StateChanged?.Invoke(this, new CoreProcessStateChangedEventArgs(previous, State, CurrentSession));
            return Task.FromResult(CurrentSession);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            State = CoreProcessState.Stopped;
            return Task.CompletedTask;
        }

        public void RaiseUnexpectedExit(int exitCode)
        {
            var processId = CurrentSession?.ProcessId ?? 0;
            if (CurrentSession is not null)
            {
                CurrentSession = CurrentSession with
                {
                    HasExited = true,
                    ExitCode = exitCode,
                    StoppedAt = DateTimeOffset.UtcNow,
                };
            }
            State = CoreProcessState.Failed;
            UnexpectedExit?.Invoke(
                this,
                new CoreProcessExitedEventArgs(processId, exitCode, expected: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
