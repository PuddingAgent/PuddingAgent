using Pudding.Contracts;
using Pudding.DesktopConnection;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 宿主组合与生命周期：启停、单实例传输占用、旧 Bridge 选择时拒绝启动、
/// 停止超时仍释放占用，以及与真实监督器的装配（假端点）。
/// </summary>
public sealed class HostTests
{
    private static DesktopCapabilityHostOptions Options(
        string desktopId = "desk-host",
        DesktopCapabilityTransportMode mode = DesktopCapabilityTransportMode.GrpcCapabilityChannel,
        TimeSpan? stopTimeout = null,
        bool enforceSingleActiveTransport = true) =>
        new()
        {
            DesktopId = new DesktopInstanceId(desktopId),
            Mode = mode,
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5),
            EnforceSingleActiveTransport = enforceSingleActiveTransport,
        };

    [Fact]
    public async Task Start_RunsSupervisorAndForwardsState()
    {
        var supervisor = new FakeSupervisor();
        await using var host = new DesktopCapabilityHost(Options(), () => supervisor);
        var states = new List<DesktopConnectionState>();
        host.StateChanged += states.Add;

        await host.StartAsync();

        Assert.True(host.IsRunning);
        await TestWait.UntilAsync(() => supervisor.Runs == 1, "supervisor loop started");

        supervisor.Generation = ConnectionGeneration.Require(4);
        supervisor.AttemptCount = 2;
        supervisor.Publish(DesktopConnectionState.Connecting);
        supervisor.Publish(DesktopConnectionState.Ready);

        Assert.True(await host.WaitForStateAsync(DesktopConnectionState.Ready, TimeSpan.FromSeconds(5)));
        Assert.Equal(DesktopConnectionState.Ready, host.State);
        Assert.Equal(4, host.Generation.Value);
        Assert.Equal(2, host.AttemptCount);
        Assert.Equal([DesktopConnectionState.Connecting, DesktopConnectionState.Ready], states);

        await host.StopAsync();
    }

    [Fact]
    public async Task Start_WhenLegacyBridgeSelected_RefusesWithoutCreatingSupervisor()
    {
        var created = 0;
        await using var host = new DesktopCapabilityHost(
            Options(mode: DesktopCapabilityTransportMode.LegacyWebSocketBridge),
            () =>
            {
                Interlocked.Increment(ref created);
                return new FakeSupervisor();
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("legacy WebSocket bridge", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, created);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_Throws()
    {
        var supervisor = new FakeSupervisor();
        await using var host = new DesktopCapabilityHost(Options(), () => supervisor);

        await host.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task SecondHost_ForTheSameDesktop_IsRejectedUntilTheFirstStops()
    {
        var first = new DesktopCapabilityHost(Options(), () => new FakeSupervisor());
        var second = new DesktopCapabilityHost(Options(), () => new FakeSupervisor());

        try
        {
            await first.StartAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());
            Assert.Contains("already active", error.Message, StringComparison.Ordinal);

            await first.StopAsync();

            // 占用必须在停止后释放，否则重启产品就再也起不来了。
            await second.StartAsync();
            Assert.True(second.IsRunning);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleActiveTransportGuard_CanBeDisabledForDiagnostics()
    {
        var first = new DesktopCapabilityHost(Options(enforceSingleActiveTransport: false), () => new FakeSupervisor());
        var second = new DesktopCapabilityHost(Options(enforceSingleActiveTransport: false), () => new FakeSupervisor());

        try
        {
            await first.StartAsync();
            await second.StartAsync();

            Assert.True(first.IsRunning);
            Assert.True(second.IsRunning);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_CancelsTheLoopAndDisposesTheSupervisor_AndIsIdempotent()
    {
        var supervisor = new FakeSupervisor();
        var host = new DesktopCapabilityHost(Options(), () => supervisor);

        await host.StartAsync();
        await host.StopAsync();
        await host.StopAsync();

        Assert.False(host.IsRunning);
        Assert.True(supervisor.Disposed);
        Assert.Equal(DesktopConnectionState.Idle, host.State);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Stop_WhenSupervisorIgnoresCancellation_TimesOutButReleasesTheClaim()
    {
        var stubborn = new FakeSupervisor { IgnoreCancellation = true };
        var host = new DesktopCapabilityHost(Options(stopTimeout: TimeSpan.FromMilliseconds(200)), () => stubborn);

        await host.StartAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.StopAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "stop must not hang on a stubborn supervisor");

        // 占用仍必须释放：后续宿主可以启动。
        await using var next = new DesktopCapabilityHost(Options(), () => new FakeSupervisor());
        await next.StartAsync();
        Assert.True(next.IsRunning);

        stubborn.Release();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task WaitForState_ReturnsFalseOnTimeout()
    {
        var supervisor = new FakeSupervisor();
        await using var host = new DesktopCapabilityHost(Options(), () => supervisor);
        await host.StartAsync();

        var reached = await host.WaitForStateAsync(
            DesktopConnectionState.Ready, TimeSpan.FromMilliseconds(150));

        Assert.False(reached);
        await host.StopAsync();
    }

    [Fact]
    public async Task DisposedHost_RejectsStart()
    {
        var host = new DesktopCapabilityHost(Options(), () => new FakeSupervisor());
        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
    }

    [Fact]
    public void Options_RejectNonPositiveStopTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCapabilityHostOptions
        {
            DesktopId = new DesktopInstanceId("desk-host"),
            StopTimeout = TimeSpan.Zero,
            Mode = DesktopCapabilityTransportMode.GrpcCapabilityChannel,
        }.Validate());
    }

    [Fact]
    public async Task Host_WithRealSupervisor_HandshakesOverTheFakeEndpoint()
    {
        // 装配验证：CreateGrpcSupervisorFactory + 真实 DesktopConnectionRunner + 假端点（不启动任何服务）。
        var streamFactory = new FakeChannelStreamFactory();
        var connectionOptions = new DesktopConnectionOptions
        {
            DesktopId = new DesktopInstanceId("desk-host"),
            ProcessInstanceId = new DesktopProcessInstanceId("proc-host"),
            SupportedCapabilities = DesktopCapability.WebViewNavigate
                | DesktopCapability.WebViewExecuteJavascript
                | DesktopCapability.ShellNotification,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        };

        await using var host = new DesktopCapabilityHost(
            Options(),
            DesktopCapabilityHost.CreateGrpcSupervisorFactory(
                streamFactory,
                new StubExecutor(),
                connectionOptions,
                initialBackoff: TimeSpan.FromMilliseconds(20),
                maxBackoff: TimeSpan.FromMilliseconds(20),
                jitter: 0));

        await host.StartAsync();
        await TestWait.UntilAsync(() => streamFactory.Stream is not null, "supervisor opened a stream");

        streamFactory.Stream!.Send(HostFrames.HelloAck(generation: 3));

        Assert.True(
            await host.WaitForStateAsync(DesktopConnectionState.Ready, TimeSpan.FromSeconds(10)),
            "host should reach Ready after the handshake ack");
        Assert.Equal(3, host.Generation.Value);

        await host.StopAsync();
        Assert.False(host.IsRunning);
    }
}
