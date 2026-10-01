using Grpc.Core;
using Pudding.Contracts;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

public sealed class HandshakeTests
{
    [Fact]
    public async Task Handshake_SendsHelloWithDeclaredCapabilities_AndBecomesReady()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        var hello = harness.Stream.Written.Single(frame => frame.Hello is not null).Hello;

        Assert.Equal("desk-1", hello.DesktopId);
        Assert.Equal("proc-1", hello.ProcessInstanceId);
        Assert.Equal((uint)DesktopProtocolVersion.Minimum, hello.SupportedVersions.Minimum);
        Assert.Equal((uint)DesktopProtocolVersion.Current, hello.SupportedVersions.Maximum);
        Assert.Equal(
            ["webview.navigate", "webview.execute_javascript", "webview.page_state", "shell.notification"],
            hello.Capabilities.Select(capability => capability.Capability).ToArray());

        Assert.Equal(DesktopConnectionState.Ready, harness.Connection.State);
        Assert.Equal(1, harness.Connection.Generation.Value);
        Assert.Equal("conn-1", harness.Connection.ConnectionId);
        Assert.Equal(1, harness.Connection.NegotiatedVersion);
        Assert.Equal(
            DesktopCapability.WebViewNavigate
                | DesktopCapability.WebViewExecuteJavascript
                | DesktopCapability.WebViewPageState
                | DesktopCapability.ShellNotification,
            harness.Connection.NegotiatedCapabilities);
    }

    [Fact]
    public async Task Handshake_AppliesCoreLimitsByTakingTheStricterOne()
    {
        var options = new HarnessOptions
        {
            MaxInFlightOperations = 8,
            MaxQueuedBytes = 1024 * 1024,
            Limits = new Proto.ChannelLimits
            {
                MaxInFlightOperations = 2,
                MaxQueuedBytes = 64 * 1024,
                MaxFrameBytes = 512 * 1024,
                HeartbeatIntervalMs = 15000,
                HeartbeatTimeoutMs = 45000,
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        Assert.Equal(2, harness.Connection.EffectiveMaxInFlight);
        Assert.Equal(64 * 1024, harness.Connection.EffectiveMaxQueuedBytes);
        Assert.NotNull(harness.Connection.NegotiatedLimits);
    }

    [Fact]
    public async Task Handshake_RejectsAckThatGrantsAnUndeclaredCapability()
    {
        var options = new HarnessOptions
        {
            Declared = DesktopCapability.WebViewNavigate,
            Granted = DesktopCapability.WebViewNavigate | DesktopCapability.ShellClipboard,
            SendHandshakeAck = false,
        };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.Send(Frames.HelloAck(options));

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, outcome.Error!.Code);
        Assert.Equal(DesktopCapability.None, harness.Connection.NegotiatedCapabilities);
    }

    [Fact]
    public async Task Handshake_RejectsUnsupportedProtocolVersion()
    {
        var options = new HarnessOptions
        {
            VersionRange = new Proto.ProtocolRange { Minimum = 5, Maximum = 9 },
            SendHandshakeAck = false,
        };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.Send(Frames.HelloAck(options));

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, outcome.Error!.Code);
    }

    [Fact]
    public async Task Handshake_RejectsNonAckFirstFrame()
    {
        var options = new HarnessOptions { SendHandshakeAck = false };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.Send(Frames.Heartbeat(1));

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, outcome.Error!.Code);
    }

    [Fact]
    public async Task Handshake_RejectsMissingGenerationOrConnectionId()
    {
        var options = new HarnessOptions { SendHandshakeAck = false };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.Send(new Proto.CoreFrame
        {
            HelloAck = new Proto.CoreHelloAck
            {
                ConnectionId = "conn-1",
                Generation = 0,
                NegotiatedVersion = new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
            },
        });

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, outcome.Error!.Code);
    }

    [Fact]
    public async Task Handshake_WhenCoreClosesStream_FaultsWithNotConnected()
    {
        var options = new HarnessOptions { SendHandshakeAck = false };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.CloseFromServer();

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, outcome.Error!.Code);
    }

    [Fact]
    public async Task Handshake_WhenCoreRejectsTheCredential_FaultsWithUnauthorized()
    {
        var options = new HarnessOptions { SendHandshakeAck = false };

        await using var harness = await ConnectionHarness.StartAsync(options);
        harness.Stream.FailFromServer(new RpcException(new Status(StatusCode.Unauthenticated, "bad control token")));

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, outcome.Error!.Code);
    }

    [Fact]
    public async Task Handshake_WhenOpenFails_FaultsWithoutThrowing()
    {
        var factory = new FakeStreamFactory(_ => throw new InvalidOperationException("no core process"));
        var audit = new RecordingAuditSink();
        var connection = new DesktopConnection(factory, new FakeExecutor(), new DesktopConnectionOptions
        {
            DesktopId = new DesktopInstanceId("desk-1"),
            ProcessInstanceId = new DesktopProcessInstanceId("proc-1"),
            SupportedCapabilities = DesktopCapability.WebViewNavigate,
            AuditSink = audit,
        });

        var outcome = await connection.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, outcome.Error!.Code);
    }

    [Fact]
    public async Task Authentication_IsSentAsMetadataAndNeverPrinted()
    {
        var authentication = DesktopChannelAuthentication.StaticHeader("x-pudding-control-token", "super-secret");
        var options = new HarnessOptions { Authentication = authentication };

        await using var harness = await ConnectionHarness.StartAsync(options);

        Assert.Equal(DesktopConnectionState.Ready, harness.Connection.State);
        Assert.Equal("super-secret", authentication.HeaderValue);
        Assert.Equal("x-pudding-control-token=***", authentication.ToString());
        Assert.DoesNotContain("super-secret", authentication.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectUnusableLimits()
    {
        static DesktopConnection Build(DesktopConnectionOptions options) =>
            new(new FakeStreamFactory(), new FakeExecutor(), options);

        static DesktopConnectionOptions Base() => new()
        {
            DesktopId = new DesktopInstanceId("desk-1"),
            ProcessInstanceId = new DesktopProcessInstanceId("proc-1"),
            SupportedCapabilities = DesktopCapability.WebViewNavigate,
        };

        Assert.Throws<ArgumentException>(() => Build(Base() with { SupportedCapabilities = DesktopCapability.None }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Base() with { MaxInFlightOperations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Base() with { MaxQueuedBytes = 512 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Base() with { TerminalResultTtl = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Base() with { RequestedMaxFrameBytes = 16 }));
    }
}
