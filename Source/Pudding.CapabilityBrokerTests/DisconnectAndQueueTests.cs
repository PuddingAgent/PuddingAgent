using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

/// <summary>断连收尾、出站队列预算与注册表清理。</summary>
public sealed class DisconnectAndQueueTests
{
    [Fact]
    public async Task DesktopClosingTheStream_CompletesPendingAsOutcomeUnknown()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        harness.Channel.PushClose();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, result.Error!.Code);
        Assert.True(result.Error.MayHaveSideEffects);

        Assert.Equal(0, harness.Session.PendingOperationCount);
        var audit = Assert.Single(harness.Audit.ForOperation(new OperationId("op-1")));
        Assert.Equal(DesktopCapabilityOutcome.Disconnected, audit.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, audit.ErrorCode);
    }

    [Fact]
    public async Task ReadFault_FaultsTheSessionAndCompletesPending()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        harness.Channel.PushFault(new IOException("pipe broke"));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, result.Error!.Code);
        Assert.Equal(DesktopLinkState.Faulted, harness.Session.State);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, harness.Session.LastError!.Code);
    }

    [Fact]
    public async Task ExplicitDisconnect_ReleasesQueuedWaitersWithNotConnected()
    {
        var options = new HarnessOptions { MaxInFlightPerConnection = 1 };
        await using var harness = await BrokerHarness.StartAsync(options);

        // 第二个调用在额度上排队（尚未发出）：断连必须让它立即结束，而不是等到自己的期限。
        var first = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        var queued = harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-2", TimeSpan.FromSeconds(60)));

        await Task.Delay(100);
        await harness.Session.DisconnectAsync(DesktopCapabilityError.Disconnected("desktop stopped"));

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, firstResult.Error!.Code);

        var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, queuedResult.Error!.Code);
        Assert.Equal(0, harness.Session.PendingOperationCount);
    }

    [Fact]
    public async Task Disconnect_ReleasesInFlightSlots()
    {
        var options = new HarnessOptions { MaxInFlightPerConnection = 2 };
        await using var harness = await BrokerHarness.StartAsync(options);

        _ = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        Assert.Equal(1, harness.Session.AvailableInFlightSlots);

        await harness.Session.DisconnectAsync(DesktopCapabilityError.Disconnected("bye"));

        Assert.Equal(2, harness.Session.AvailableInFlightSlots);
    }

    [Fact]
    public async Task OutboundQueueFullUntilDeadline_ReturnsResourceExhausted()
    {
        var options = new HarnessOptions { MaxQueuedFrames = 2, MaxInFlightPerConnection = 8 };
        await using var harness = await BrokerHarness.StartAsync(options);
        harness.Channel.BlockSends = true;

        var tasks = new List<Task<CapabilityResult<NavigateResult>>>();
        for (var index = 0; index < 5; index++)
        {
            tasks.Add(harness.Session.NavigateAsync(
                BrokerHarness.Navigate($"https://example.com/{index}"),
                harness.Call($"op-{index}", TimeSpan.FromMilliseconds(300))));
        }

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        harness.Channel.ReleaseSends();

        Assert.Contains(results, result => result.Error?.Code == DesktopCapabilityErrorCode.ResourceExhausted);
        Assert.All(results, result => Assert.NotNull(result.Error));
    }

    [Fact]
    public async Task Audit_RecordsOneTerminalRecordPerOperation()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var ok = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-ok"));
        await harness.WaitForCommandAsync("op-ok");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-ok", 1)));
        Assert.True((await ok.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        var failed = harness.Session.ExecuteJavascriptAsync(BrokerHarness.Javascript(), harness.Call("op-fail"));
        await harness.WaitForCommandAsync("op-fail");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.Error("op-fail", 1, "unauthorized")));
        Assert.True((await failed.WaitAsync(TimeSpan.FromSeconds(5))).IsFailure);

        Assert.Single(harness.Audit.ForOperation(new OperationId("op-ok")));
        Assert.Single(harness.Audit.ForOperation(new OperationId("op-fail")));
        Assert.Equal(2, harness.Audit.Records.Count);
    }

    [Fact]
    public async Task Registry_RemovesTheSessionAfterItCompletes()
    {
        await using var harness = await BrokerHarness.StartAsync();
        Assert.Equal(1, harness.Broker.Sessions.Count);

        harness.Channel.PushClose();

        await TestWait.UntilAsync(() => harness.Broker.Sessions.Count == 0, "session removed from the registry");
        Assert.Null(harness.Broker.Find(new DesktopInstanceId("desk-1")));
    }

    [Fact]
    public async Task BrokerDispose_DisconnectsActiveSessions()
    {
        var harness = await BrokerHarness.StartAsync();

        await harness.Broker.DisposeAsync();

        await TestWait.UntilAsync(
            () => harness.Session.State is DesktopLinkState.Disconnected or DesktopLinkState.Faulted,
            "session closed by broker dispose");
        Assert.Empty(harness.Broker.Sessions);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task SecondHello_FaultsTheSession()
    {
        await using var harness = await BrokerHarness.StartAsync();

        harness.Channel.Push(BrokerHarness.Hello(harness.Options));

        await TestWait.UntilAsync(
            () => harness.Session.State == DesktopLinkState.Faulted, "second hello faults the session");
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, harness.Session.LastError!.Code);
    }

    [Fact]
    public async Task EmptyFrame_FaultsTheSession()
    {
        await using var harness = await BrokerHarness.StartAsync();

        harness.Channel.Push(new Proto.DesktopFrame());

        await TestWait.UntilAsync(
            () => harness.Session.State == DesktopLinkState.Faulted, "empty frame faults the session");
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, harness.Session.LastError!.Code);
    }
}
