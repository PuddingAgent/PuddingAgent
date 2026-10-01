using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

/// <summary>握手协商、单实例传输与注册表。</summary>
public sealed class NegotiationAndRegistryTests
{
    [Fact]
    public async Task Accept_NegotiatesAndSendsAck()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var ack = harness.Channel.Sent.Single(frame => frame.HelloAck is not null).HelloAck;

        Assert.Equal(1UL, ack.Generation);
        Assert.Equal("core-", ack.ConnectionId[..5]);
        Assert.Equal(1u, ack.NegotiatedVersion.Minimum);
        Assert.Equal((uint)DesktopProtocolVersion.Current, ack.NegotiatedVersion.Maximum);
        Assert.Equal(
            ["webview.navigate", "webview.execute_javascript", "webview.page_state", "shell.notification", "shell.status"],
            ack.Capabilities.Select(declaration => declaration.Capability).ToArray());
        Assert.Equal(8u, ack.Limits.MaxInFlightOperations);
        Assert.False(string.IsNullOrEmpty(ack.CoreInstanceId));

        Assert.Equal(DesktopLinkState.Ready, harness.Session.State);
        Assert.Equal(1, harness.Broker.Sessions.Count);
        Assert.Same(harness.Session, harness.Broker.Find(new DesktopInstanceId("desk-1")));
    }

    [Fact]
    public async Task Accept_GrantsOnlyTheDeclaredIntersectionWithLocalPolicy()
    {
        var options = new HarnessOptions
        {
            Declared = DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification,
            Grantable = DesktopCapability.WebViewNavigate
                | DesktopCapability.WebViewExecuteJavascript
                | DesktopCapability.WebViewPageState,
        };

        await using var harness = await BrokerHarness.StartAsync(options);

        var ack = harness.Channel.Sent.Single(frame => frame.HelloAck is not null).HelloAck;
        Assert.Equal(["webview.navigate"], ack.Capabilities.Select(declaration => declaration.Capability).ToArray());
        Assert.Equal(DesktopCapability.WebViewNavigate, harness.Session.NegotiatedCapabilities);
    }

    [Fact]
    public async Task Accept_SucceedsWithNoNegotiatedCapability()
    {
        var options = new HarnessOptions
        {
            Declared = DesktopCapability.ShellDialog,
            Grantable = DesktopCapability.WebViewNavigate,
        };

        await using var harness = await BrokerHarness.StartAsync(options);

        Assert.Equal(DesktopCapability.None, harness.Session.NegotiatedCapabilities);

        // 没有任何协商成功的能力 ⇒ 调用必须明确拒绝，而不是假装可用。
        var result = await harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-none"));

        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, result.Error.Code);
    }

    [Fact]
    public async Task Accept_RejectsUnexpectedDesktopIdentity()
    {
        var channel = new FakeDesktopChannel();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate },
        });

        var hello = BrokerHarness.Hello(new HarnessOptions { DesktopId = "desk-other" });
        channel.Push(hello);

        var accepted = await broker.AcceptAsync(channel);

        Assert.True(accepted.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, accepted.Error.Code);
        Assert.Equal(0, broker.Sessions.Count);
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Accept_RejectsNonHelloFirstFrame()
    {
        var channel = new FakeDesktopChannel();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate },
        });

        channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 1)));

        var accepted = await broker.AcceptAsync(channel);

        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, accepted.Error!.Code);
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Accept_RejectsUnsupportedProtocolVersion()
    {
        var channel = new FakeDesktopChannel();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate },
        });

        channel.Push(new Proto.DesktopFrame
        {
            Hello = new Proto.DesktopHello
            {
                DesktopId = "desk-1",
                ProcessInstanceId = "proc-1",
                SupportedVersions = new Proto.ProtocolRange { Minimum = 5, Maximum = 9 },
            },
        });

        var accepted = await broker.AcceptAsync(channel);

        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, accepted.Error!.Code);
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Accept_WhenDesktopClosesStreamBeforeHello_IsNotConnected()
    {
        var channel = new FakeDesktopChannel();
        channel.PushClose();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate },
        });

        var accepted = await broker.AcceptAsync(channel);

        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, accepted.Error!.Code);
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Accept_WhenHelloNeverArrives_TimesOut()
    {
        var channel = new FakeDesktopChannel();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy
            {
                Grantable = DesktopCapability.WebViewNavigate,
                HandshakeTimeout = TimeSpan.FromMilliseconds(150),
            },
        });

        var accepted = await broker.AcceptAsync(channel);

        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, accepted.Error!.Code);
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Accept_RejectsSecondActiveTransportForTheSameDesktop()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var secondChannel = new FakeDesktopChannel();
        secondChannel.Push(BrokerHarness.Hello(harness.Options));

        var accepted = await harness.Broker.AcceptAsync(secondChannel);

        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, accepted.Error!.Code);
        Assert.Equal(1, harness.Broker.Sessions.Count);
    }

    [Fact]
    public async Task Accept_AfterThePreviousSessionEnds_AdvancesTheGeneration()
    {
        await using var harness = await BrokerHarness.StartAsync();

        await harness.Session.DisconnectAsync(DesktopCapabilityError.Disconnected("desktop restarted"));
        await TestWait.UntilAsync(() => harness.Broker.Sessions.Count == 0, "session removed from the registry");

        var secondChannel = new FakeDesktopChannel();
        secondChannel.Push(BrokerHarness.Hello(harness.Options));

        var accepted = await harness.Broker.AcceptAsync(secondChannel);

        Assert.True(accepted.IsSuccess);
        // 新连接必须拿到更高的世代：旧 PageId/Snapshot ref 由此作废。
        Assert.Equal(2, accepted.Value.Generation.Value);

        await accepted.Value.DisconnectAsync(DesktopCapabilityError.Disconnected("teardown"));
    }

    [Fact]
    public async Task Policy_RejectsUnusableLimits()
    {
        Assert.Throws<ArgumentException>(() =>
            new DesktopCapabilityPolicy { Grantable = DesktopCapability.None }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate, MaxQueuedFrames = 0 }.Validate());

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Broker_RejectsAcceptAfterDispose()
    {
        var channel = new FakeDesktopChannel();
        var broker = new CapabilityBroker(new CapabilityBrokerOptions
        {
            HostDesktopId = new DesktopInstanceId("desk-1"),
            Policy = new DesktopCapabilityPolicy { Grantable = DesktopCapability.WebViewNavigate },
        });

        await broker.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.AcceptAsync(channel));
    }
}
