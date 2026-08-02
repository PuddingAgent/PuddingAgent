using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Core;

public class CoreProcessSupervisorTests
{
    [Fact]
    public async Task StartAsync_DuplicateStart_PreventsSecondCall()
    {
        var supervisor = new CoreProcessSupervisor();
        var options = new CoreProcessStartOptions
        {
            ExecutablePath = "nonexistent.exe",
            DataRoot = "D:\\data",
            ParentProcessId = 1234,
            ControlToken = "test-token",
            StartupTimeout = TimeSpan.FromMilliseconds(100),
        };

        // First start fails because process doesn't exist
        await Assert.ThrowsAnyAsync<Exception>(
            () => supervisor.StartAsync(options, CancellationToken.None));

        // Second start should also fail (state is Failed, not Idle/Ready)
        // The supervisor should reject duplicate start attempts
        await Assert.ThrowsAnyAsync<Exception>(
            () => supervisor.StartAsync(options, CancellationToken.None));

        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task Stop_WhenIdle_IsIdempotent()
    {
        var supervisor = new CoreProcessSupervisor();
        Assert.Equal(CoreProcessState.Idle, supervisor.State);

        await supervisor.StopAsync(CancellationToken.None);
        Assert.Equal(CoreProcessState.Idle, supervisor.State);

        await supervisor.StopAsync(CancellationToken.None);
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task StateChanged_FiresOnTransition()
    {
        var supervisor = new CoreProcessSupervisor();
        var events = new List<CoreProcessState>();
        supervisor.StateChanged += (_, e) => events.Add(e.Current);

        var options = new CoreProcessStartOptions
        {
            ExecutablePath = "nonexistent.exe",
            DataRoot = "D:\\data",
            ParentProcessId = 1234,
            ControlToken = "test-token",
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };

        try { await supervisor.StartAsync(options, CancellationToken.None); }
        catch { }

        // Should have transitioned: Idle → Starting → Failed
        Assert.Contains(CoreProcessState.Starting, events);
        Assert.Contains(CoreProcessState.Failed, events);

        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        var supervisor = new CoreProcessSupervisor();

        var options = new CoreProcessStartOptions
        {
            ExecutablePath = "nonexistent.exe",
            DataRoot = "D:\\data",
            ParentProcessId = 1234,
            ControlToken = "test-token",
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };

        try { await supervisor.StartAsync(options, CancellationToken.None); } catch { }

        // Dispose should not throw even if process was never started
        await supervisor.DisposeAsync();

        Assert.True(supervisor.State is CoreProcessState.Failed or CoreProcessState.Stopped);
    }

    [Fact]
    public async Task StartAsync_Cancellation_DoesNotCrash()
    {
        var supervisor = new CoreProcessSupervisor();
        var cts = new CancellationTokenSource();
        var options = new CoreProcessStartOptions
        {
            ExecutablePath = "nonexistent.exe",
            DataRoot = "D:\\data",
            ParentProcessId = 1234,
            ControlToken = "test-token",
            StartupTimeout = TimeSpan.FromSeconds(30),
        };

        cts.Cancel();

        // Cancelled before lock acquisition → OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => supervisor.StartAsync(options, cts.Token));

        // State remains Idle (lock wasn't acquired)
        Assert.Equal(CoreProcessState.Idle, supervisor.State);

        await supervisor.DisposeAsync();
    }

    [Fact]
    public void CoreProcessStartOptions_ControlToken_NotDefaulted()
    {
        var options = new CoreProcessStartOptions
        {
            ExecutablePath = "test.exe",
            DataRoot = "D:\\data",
            ParentProcessId = 1,
            ControlToken = "secret-token-abc",
        };

        Assert.Equal("secret-token-abc", options.ControlToken);
    }

    [Fact]
    public void CreateProcessStartInfo_IsolatesDesktopChildEnvironment()
    {
        var executablePath = Path.Combine(
            Path.GetTempPath(),
            "pudding-desktop-tests",
            "core",
            "PuddingAgent.exe");
        var options = new CoreProcessStartOptions
        {
            ExecutablePath = executablePath,
            DataRoot = "D:\\data",
            Port = 0,
            ParentProcessId = 1234,
            ControlToken = "test-token",
        };

        var startInfo = CoreProcessSupervisor.CreateProcessStartInfo(options);

        Assert.Equal("Production", startInfo.Environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Production", startInfo.Environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal(Path.GetDirectoryName(executablePath), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Contains("--desktop-child", startInfo.ArgumentList);
    }

    [Fact]
    public void LogBuffer_Capacity_Enforced()
    {
        var buffer = new CoreProcessLogBuffer(3);
        buffer.Append("line1"); buffer.Append("line2");
        buffer.Append("line3"); buffer.Append("line4");

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal("line2", snapshot[0]);
        Assert.Equal("line4", snapshot[2]);
    }

    [Fact]
    public void LogBuffer_Tail_ReturnsLastLines()
    {
        var buffer = new CoreProcessLogBuffer(5);
        for (var i = 1; i <= 10; i++) buffer.Append($"line{i}");

        var tail = buffer.GetTail(3);
        Assert.Contains("line8", tail);
        Assert.Contains("line10", tail);
    }
}
