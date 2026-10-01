using Pudding.Contracts;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

public sealed class RunnerTests
{
    private static DesktopConnectionOptions Options() => new()
    {
        DesktopId = new DesktopInstanceId("desk-1"),
        ProcessInstanceId = new DesktopProcessInstanceId("proc-1"),
        SupportedCapabilities =
            DesktopCapability.WebViewNavigate | DesktopCapability.WebViewExecuteJavascript | DesktopCapability.ShellNotification,
        HandshakeTimeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task Runner_RetriesWithBackoffAndResetsAfterAHealthyConnection()
    {
        var factory = new FakeStreamFactory(index => index <= 2
            ? throw new InvalidOperationException("core is not listening")
            : new FakeCoreStream());

        var delays = new List<TimeSpan>();
        using var cancellation = new CancellationTokenSource();
        var runner = new DesktopConnectionRunner(
            factory,
            new FakeExecutor(),
            Options(),
            initialBackoff: TimeSpan.FromSeconds(1),
            maxBackoff: TimeSpan.FromSeconds(8),
            jitter: 0.5,
            maxAttempts: 4,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            jitterSample: () => 0.5);

        var runTask = runner.RunAsync(cancellation.Token);

        await TestWait.UntilAsync(() => factory.Streams.Count == 1, "third attempt opened a stream");
        var healthy = factory.Streams[0];
        await healthy.WaitForWriteAsync(frame => frame.Hello is not null, "hello on the healthy attempt");
        healthy.Send(Frames.HelloAck(connectionId: "conn-3", generation: 7));
        await TestWait.UntilAsync(() => runner.State == DesktopConnectionState.Ready, "runner reports Ready");

        healthy.CloseFromServer();
        await TestWait.UntilAsync(() => factory.Streams.Count == 2, "runner reconnected after the drop");
        var reconnected = factory.Streams[1];
        await reconnected.WaitForWriteAsync(frame => frame.Hello is not null, "hello after reconnect");

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, runner.AttemptCount);
        Assert.Equal(7, runner.Generation.Value);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)],
            runner.BackoffHistory.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(4), TimeSpan.FromTicks(delays.Sum(delay => delay.Ticks)));
    }

    [Fact]
    public async Task Runner_DoesNotReplayCommandsAfterReconnect()
    {
        var factory = new FakeStreamFactory();
        var executor = new FakeExecutor();
        using var cancellation = new CancellationTokenSource();

        var runner = new DesktopConnectionRunner(
            factory,
            executor,
            Options(),
            initialBackoff: TimeSpan.FromMilliseconds(20),
            maxBackoff: TimeSpan.FromMilliseconds(20),
            jitter: 0,
            maxAttempts: 3,
            delay: static (_, _) => Task.CompletedTask);

        var runTask = runner.RunAsync(cancellation.Token);

        await TestWait.UntilAsync(() => factory.Streams.Count == 1, "first stream");
        var first = factory.Streams[0];
        await first.WaitForWriteAsync(frame => frame.Hello is not null, "hello on first attempt");
        first.Send(Frames.HelloAck(generation: 1));
        await TestWait.UntilAsync(() => runner.State == DesktopConnectionState.Ready, "first attempt ready");

        first.Send(Frames.NavigateCommand("op-1", generation: 1));
        await first.WaitForResultAsync("op-1");
        Assert.Equal(1, executor.CallCount);

        first.CloseFromServer();
        await TestWait.UntilAsync(() => factory.Streams.Count == 2, "second stream");
        var second = factory.Streams[1];
        await second.WaitForWriteAsync(frame => frame.Hello is not null, "hello on second attempt");
        second.Send(Frames.HelloAck(connectionId: "conn-2", generation: 2));
        await TestWait.UntilAsync(() => runner.Generation.Value == 2, "generation advanced");

        // 重连不得自动重放副作用命令：新连接上只有握手帧。
        Assert.All(second.Written, frame => Assert.NotNull(frame.Hello));
        Assert.Equal(1, executor.CallCount);

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Runner_StopsOnCancellation()
    {
        var factory = new FakeStreamFactory(_ => throw new InvalidOperationException("core is down"));
        using var cancellation = new CancellationTokenSource();
        var delays = new List<TimeSpan>();

        var runner = new DesktopConnectionRunner(
            factory,
            new FakeExecutor(),
            Options(),
            initialBackoff: TimeSpan.FromSeconds(1),
            maxBackoff: TimeSpan.FromSeconds(4),
            jitter: 0,
            delay: (duration, token) =>
            {
                delays.Add(duration);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runner.AttemptCount);
        Assert.Single(delays);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, runner.LastError!.Code);
        Assert.Equal(DesktopConnectionState.Disconnected, runner.State);
    }

    [Fact]
    public async Task Runner_WhenMaxAttemptsReached_StopsRetrying()
    {
        var factory = new FakeStreamFactory(_ => throw new InvalidOperationException("core is down"));
        using var cancellation = new CancellationTokenSource();

        var runner = new DesktopConnectionRunner(
            factory,
            new FakeExecutor(),
            Options(),
            initialBackoff: TimeSpan.FromMilliseconds(1),
            maxBackoff: TimeSpan.FromMilliseconds(4),
            jitter: 0,
            maxAttempts: 3,
            delay: static (_, _) => Task.CompletedTask);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, runner.AttemptCount);
        Assert.Equal(2, runner.BackoffHistory.Count);
    }

    [Fact]
    public void Runner_RejectsInvalidBackoffBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopConnectionRunner(
            new FakeStreamFactory(),
            new FakeExecutor(),
            Options(),
            initialBackoff: TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopConnectionRunner(
            new FakeStreamFactory(),
            new FakeExecutor(),
            Options(),
            initialBackoff: TimeSpan.FromSeconds(5),
            maxBackoff: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Transport_RequiresLoopbackForPlaintextHttp2()
    {
        Assert.Equal(DesktopChannelTransportKind.NamedPipe, DesktopChannelTransport.NamedPipe("pudding-desktop-1").Kind);
        Assert.Equal(DesktopChannelTransportKind.LoopbackHttp2, DesktopChannelTransport.LoopbackHttp2("http://127.0.0.1:5099").Kind);
        Assert.Equal(DesktopChannelTransportKind.Tls, DesktopChannelTransport.Tls("https://core.example:5099").Kind);

        Assert.Throws<ArgumentException>(() => DesktopChannelTransport.LoopbackHttp2("http://10.0.0.5:5099"));
        Assert.Throws<ArgumentException>(() => DesktopChannelTransport.LoopbackHttp2("https://127.0.0.1:5099"));
        Assert.Throws<ArgumentException>(() => DesktopChannelTransport.Tls("http://127.0.0.1:5099"));
        Assert.Throws<ArgumentException>(() => DesktopChannelTransport.NamedPipe("  "));
    }

    [Fact]
    public void ChannelFactory_NeverPrintsCredentials()
    {
        var transport = DesktopChannelTransport.NamedPipe("pudding-desktop");
        Assert.Equal("NamedPipe:pudding-desktop", transport.ToString());
        Assert.DoesNotContain("token", transport.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GrpcFactory_CreatesChannelForEveryTransportKind()
    {
        using var pipeChannel = GrpcDesktopChannelStreamFactory.CreateChannel(
            DesktopChannelTransport.NamedPipe("pudding-desktop"), 1024 * 1024);
        using var loopbackChannel = GrpcDesktopChannelStreamFactory.CreateChannel(
            DesktopChannelTransport.LoopbackHttp2("http://127.0.0.1:5099"), 1024 * 1024);

        Assert.NotNull(pipeChannel);
        Assert.NotNull(loopbackChannel);
        Assert.NotNull(pipeChannel.Target);
    }
}
